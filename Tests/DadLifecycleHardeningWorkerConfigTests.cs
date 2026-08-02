using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLifecycleHardeningWorkerConfigTests
{
    [Fact]
    public void WorkerQueuePreservesSameRunFifoAndRejectsAnotherRun()
    {
        var queue = new DadWorkerRunCommandQueue();

        Assert.Equal(DadWorkerRunQueueAdmission.Enqueued, queue.Enqueue(Command("a", "run-a"), out _));
        Assert.Equal(DadWorkerRunQueueAdmission.Enqueued, queue.Enqueue(Command("b", "run-a"), out _));
        Assert.Equal(DadWorkerRunQueueAdmission.Rejected, queue.Enqueue(Command("c", "run-b"), out var blocker));
        Assert.Contains("run-a", blocker, StringComparison.OrdinalIgnoreCase);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("a", first.CommandId);
        queue.ReleaseOwnershipIfIdle(hasActiveCommand: true);
        Assert.Equal("run-a", queue.OwnedRunId);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("b", second.CommandId);
        queue.ReleaseOwnershipIfIdle(hasActiveCommand: false);
        Assert.Equal(string.Empty, queue.OwnedRunId);
        Assert.Equal(DadWorkerRunQueueAdmission.Enqueued, queue.Enqueue(Command("c", "run-b"), out _));
    }

    [Fact]
    public void WorkerCancellationDrainsCompleteRunBeforeOwnershipRelease()
    {
        var queue = new DadWorkerRunCommandQueue();
        queue.Enqueue(Command("a", "run-a"), out _);
        queue.Enqueue(Command("b", "run-a"), out _);

        var cancelled = queue.DrainRun("RUN-A");
        queue.ReleaseOwnershipIfIdle(hasActiveCommand: false);

        Assert.Equal(["a", "b"], cancelled.Select(static command => command.CommandId));
        Assert.True(queue.IsEmpty);
        Assert.Equal(string.Empty, queue.OwnedRunId);
        Assert.Equal(DadWorkerRunQueueAdmission.Enqueued, queue.Enqueue(Command("c", "run-b"), out _));
    }

    [Fact]
    public void HistoricalDuplicateUsesFrozenTruthBeforeImmutableCollisionCheck()
    {
        var command = HistoricalCommand();
        var frozenRuntime = Assert.Single(
            command.Participants,
            static participant => participant.IsLocalClient).Clone();

        Assert.True(DadWorkerCommandValidationRules.TryValidate(
            command,
            frozenRuntime,
            out _,
            out var initialBlocker), initialBlocker);

        var changedRuntime = frozenRuntime.Clone();
        changedRuntime.WorkerSessionId = new DadWorkerSessionId("replacement-worker");
        changedRuntime.IsAvailable = false;
        changedRuntime.State = DadParticipantState.Stale;
        Assert.False(DadWorkerCommandValidationRules.TryValidate(
            command,
            changedRuntime,
            out _,
            out _));
        Assert.True(DadWorkerRecordedCommandValidationRules.TryValidate(
            command,
            out var historicalBlocker), historicalBlocker);

        var registry = new DadImmutableCommandRegistry();
        var payload = DadIpcJson.Serialize(command);
        Assert.Equal(
            DadImmutableCommandDisposition.Accepted,
            registry.Register(command.CommandId, payload, payload, "original").Disposition);

        var mutated = Assert.IsType<DadWorkerExecutionCommand>(
            DadIpcJson.Deserialize<DadWorkerExecutionCommand>(payload));
        mutated.Plan.Summary = "mutated after historical completion";
        Assert.True(DadWorkerRecordedCommandValidationRules.TryValidate(
            mutated,
            out var mutatedBlocker), mutatedBlocker);
        var mutatedPayload = DadIpcJson.Serialize(mutated);
        Assert.Equal(
            DadImmutableCommandDisposition.Collision,
            registry.Register(
                mutated.CommandId,
                mutatedPayload,
                mutatedPayload,
                "duplicate").Disposition);
    }

    [Fact]
    public void FailedAccountPersistenceRestoresCompleteInMemoryGraphAndRevisions()
    {
        var account = Account("account-a", "Before");
        var originalDefaultRevision = account.DefaultConfig.Revision;
        var originalCharacterRevision = account.Characters["Alpha@World"].Revision;

        var saved = DadAccountConfigPersistence.TryApply(
            account,
            () =>
            {
                account.AccountAlias = "After";
                account.Revision++;
                account.DefaultConfig.Revision++;
                account.DefaultConfig.TargetNotes = "changed";
                account.Characters["Alpha@World"].Revision++;
                account.Characters["Beta@World"] = new CharacterConfig { Revision = 9 };
            },
            persist: () => false);

        Assert.False(saved);
        Assert.Equal("Before", account.AccountAlias);
        Assert.Equal(4, account.Revision);
        Assert.Equal(originalDefaultRevision, account.DefaultConfig.Revision);
        Assert.Equal("original", account.DefaultConfig.TargetNotes);
        Assert.Equal(originalCharacterRevision, account.Characters["Alpha@World"].Revision);
        Assert.DoesNotContain("Beta@World", account.Characters.Keys);
    }

    [Fact]
    public void CorruptAccountFileDoesNotSuppressLaterValidFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dad-account-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "a-corrupt_dad.json"), "{not-json");
            File.WriteAllText(
                Path.Combine(directory, "z-valid_dad.json"),
                JsonSerializer.Serialize(Account("valid", "Valid")));
            var failures = new List<string>();

            var loaded = DadAccountConfigPersistence.LoadAll(
                directory,
                new JsonSerializerOptions(),
                (path, _) => failures.Add(path));

            Assert.Equal("valid", Assert.Single(loaded).AccountId);
            Assert.Single(failures);
            Assert.EndsWith("a-corrupt_dad.json", failures[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DadWorkerExecutionCommand Command(string commandId, string runId)
        => new()
        {
            CommandId = commandId,
            RunId = runId,
        };

    private static DadWorkerExecutionCommand HistoricalCommand()
    {
        const string runId = "historical-run";
        var orchestration = new DadOrchestrationIntent
        {
            ModuleTarget = DadModuleId.Duty,
            RequirePostArReady = true,
        };
        return new DadWorkerExecutionCommand
        {
            CommandId = "historical-command",
            RunId = runId,
            ModuleIndex = 0,
            Role = DadWorkerExecutionRole.QueueLeader,
            Plan = new DadRunPlan
            {
                Request = new DadRunRequest
                {
                    RequestId = runId,
                    RequestedBy = "historical-test",
                    Orchestration = orchestration,
                },
                CompositeModuleId = DadModuleId.Duty,
                Orchestration = orchestration,
                RequiredParticipantCount = 1,
                Modules =
                [
                    new DadPlannedModuleExecution
                    {
                        ModuleId = DadModuleId.Duty,
                        DisplayName = "Local Duty",
                    },
                ],
            },
            Participants =
            [
                new DadParticipantSnapshot
                {
                    WorkerSessionId = new DadWorkerSessionId("historical-worker"),
                    IsLocalClient = true,
                    IsAvailable = true,
                    IsEligibleForRun = true,
                    PostArReady = true,
                    State = DadParticipantState.Ready,
                },
            ],
        };
    }

    private static AccountConfig Account(string accountId, string alias)
        => new()
        {
            AccountId = accountId,
            AccountAlias = alias,
            Revision = 4,
            DefaultConfig = new CharacterConfig
            {
                Revision = 3,
                TargetNotes = "original",
            },
            Characters = new Dictionary<string, CharacterConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alpha@World"] = new CharacterConfig { Revision = 2 },
            },
        };
}
