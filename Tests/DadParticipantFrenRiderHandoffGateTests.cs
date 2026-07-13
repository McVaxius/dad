using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadParticipantFrenRiderHandoffGateTests
{
    private const string RunId = "run-sastasha";
    private const string WAccount = "account-w";
    private const string XAccount = "account-x";
    private const string WCharacter = "Venat O'Azem@Excalibur";
    private const string XCharacter = "X Character@Excalibur";
    private const string WWorker = "worker-w";
    private const string XWorker = "worker-x";
    private const ulong WContentId = 1001;
    private const ulong XContentId = 2002;
    private static readonly DateTime Start = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExactDutyEntryConfiguresFrozenApostropheTargetOnce()
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var command = BuildCommand();
        var calls = new List<string>();

        var waiting = gate.Apply(
            command,
            useFrenRider: true,
            exactRequestedDutyEntered: false,
            Start,
            _ => throw new InvalidOperationException("must not invoke before exact duty"),
            out _);
        var configured = gate.Apply(
            command,
            useFrenRider: true,
            exactRequestedDutyEntered: true,
            Start,
            target =>
            {
                calls.Add(target);
                return DadFrenRiderCommandResult.Success();
            },
            out var configuredSummary);
        var repeated = gate.Apply(
            command,
            useFrenRider: true,
            exactRequestedDutyEntered: true,
            Start.AddSeconds(1),
            _ => throw new InvalidOperationException("must be idempotent"),
            out var repeatedSummary);

        Assert.Equal(DadParticipantFrenRiderHandoffStatus.WaitingForExactDuty, waiting);
        Assert.Equal(DadParticipantFrenRiderHandoffStatus.Configured, configured);
        Assert.Equal(DadParticipantFrenRiderHandoffStatus.AlreadyConfigured, repeated);
        Assert.Equal([WCharacter], calls);
        Assert.Contains(WCharacter, configuredSummary, StringComparison.Ordinal);
        Assert.Equal(configuredSummary, repeatedSummary);
    }

    [Fact]
    public void ReversedAssignmentRowsStillSelectSlot1AndNeverSlot2()
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var command = BuildCommand();
        command.Participants.Reverse();
        var calls = new List<string>();

        var result = gate.Apply(
            command,
            useFrenRider: true,
            exactRequestedDutyEntered: true,
            Start,
            target =>
            {
                calls.Add(target);
                return DadFrenRiderCommandResult.Success();
            },
            out _);

        Assert.Equal(DadParticipantFrenRiderHandoffStatus.Configured, result);
        Assert.Equal([WCharacter], calls);
        Assert.DoesNotContain(XCharacter, calls);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("slot")]
    [InlineData("account")]
    [InlineData("character-account")]
    [InlineData("active-character")]
    [InlineData("character")]
    [InlineData("missing-character")]
    [InlineData("content")]
    [InlineData("local")]
    [InlineData("authority")]
    public void IdentityInvalidSlot1FailsBeforeIpc(string mismatch)
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var command = BuildCommand();
        var target = command.Participants.Single(static participant => participant.AssignedSlotId == "Slot1");
        switch (mismatch)
        {
            case "run":
                target.RunId = "wrong-run";
                break;
            case "session":
                target.WorkerSessionId = new DadWorkerSessionId(XWorker);
                break;
            case "slot":
                target.AssignedSlotId = "Slot9";
                break;
            case "account":
                target.ManagedAccountKey = new DadAccountKey("wrong-account");
                break;
            case "character-account":
                target.Character.AccountId = "wrong-account";
                break;
            case "active-character":
                target.ActiveCharacterKey = new DadCharacterKey("Wrong Character@Excalibur");
                break;
            case "character":
                target.Character.CharacterKey = "Wrong Character@Excalibur";
                break;
            case "missing-character":
                target.Character = null!;
                break;
            case "content":
                target.Character.ContentId = 9999;
                break;
            case "local":
                target.IsLocalClient = true;
                break;
            case "authority":
                target.IsAuthority = false;
                break;
        }

        var callCount = 0;
        var result = gate.Apply(
            command,
            useFrenRider: true,
            exactRequestedDutyEntered: true,
            Start,
            _ =>
            {
                callCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out var summary);

        Assert.Equal(DadParticipantFrenRiderHandoffStatus.Failed, result);
        Assert.Equal(0, callCount);
        Assert.Contains("rejected", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOrDuplicatedSlot1FailsBeforeIpc()
    {
        var missing = BuildCommand();
        missing.Participants.RemoveAll(static participant => participant.AssignedSlotId == "Slot1");
        AssertTargetRejected(missing);

        var duplicated = BuildCommand();
        duplicated.Participants.Single(static participant => participant.IsLocalClient).AssignedSlotId = "Slot1";
        AssertTargetRejected(duplicated);
    }

    [Theory]
    [InlineData("Venat O'Azem")]
    [InlineData(" Venat O'Azem@Excalibur")]
    [InlineData("Venat O'Azem@Excalibur ")]
    [InlineData("Venat O'Azem @Excalibur")]
    [InlineData("Venat O'Azem@ Excalibur")]
    [InlineData("Venat O'Azem@Crystal Tower")]
    [InlineData("Venat O'Azem@@Excalibur")]
    public void MalformedNameAtWorldFailsBeforeIpc(string malformed)
    {
        var command = BuildCommand();
        command.Plan.Orchestration.RequiredRosterCharacters[0].CharacterKey = new DadCharacterKey(malformed);
        command.Plan.LeaderCharacterKey = malformed;
        command.Plan.InviterCharacterKey = malformed;
        var target = command.Participants.Single(static participant => participant.AssignedSlotId == "Slot1");
        target.ActiveCharacterKey = new DadCharacterKey(malformed);
        target.Character.CharacterKey = malformed;

        AssertTargetRejected(command);
    }

    [Fact]
    public void NonExactDutyTruthAndDisabledModesNeverInvokeIpc()
    {
        var command = BuildCommand();
        var callCount = 0;
        DadFrenRiderCommandResult Invoke(string _)
        {
            callCount++;
            return DadFrenRiderCommandResult.Success();
        }

        foreach (var scenario in new[] { "queue-wait", "commence", "transition", "generic-bound", "wrong-duty" })
        {
            var gate = new DadParticipantFrenRiderHandoffGate();
            Assert.Equal(
                DadParticipantFrenRiderHandoffStatus.WaitingForExactDuty,
                gate.Apply(command, true, false, Start, Invoke, out _));
        }

        // Both DoNothing and ForceCommands are represented by useFrenRider=false at the worker boundary.
        for (var mode = 0; mode < 2; mode++)
        {
            var gate = new DadParticipantFrenRiderHandoffGate();
            Assert.Equal(
                DadParticipantFrenRiderHandoffStatus.NotRequired,
                gate.Apply(command, false, true, Start, Invoke, out _));
        }

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void FalseResponseRetriesOncePerSecondAndFailsAtFiveSeconds()
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var command = BuildCommand();
        var callCount = 0;
        DadFrenRiderCommandResult Reject(string _)
        {
            callCount++;
            return DadFrenRiderCommandResult.Failure("IPC rejected request");
        }

        Assert.Equal(
            DadParticipantFrenRiderHandoffStatus.PendingRetry,
            gate.Apply(command, true, true, Start, Reject, out _));
        Assert.Equal(
            DadParticipantFrenRiderHandoffStatus.PendingRetry,
            gate.Apply(command, true, true, Start.AddMilliseconds(999), Reject, out _));
        Assert.Equal(1, callCount);

        for (var second = 1; second < 5; second++)
        {
            Assert.Equal(
                DadParticipantFrenRiderHandoffStatus.PendingRetry,
                gate.Apply(command, true, true, Start.AddSeconds(second), Reject, out _));
        }

        var terminal = gate.Apply(command, true, true, Start.AddSeconds(5), Reject, out var summary);
        Assert.Equal(DadParticipantFrenRiderHandoffStatus.Failed, terminal);
        Assert.Equal(6, callCount);
        Assert.Contains("failed after five seconds", summary, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            DadParticipantFrenRiderHandoffStatus.Failed,
            gate.Apply(command, true, true, Start.AddSeconds(6), Reject, out _));
        Assert.Equal(6, callCount);
    }

    [Fact]
    public void ExceptionRetriesAndSuccessStopsFurtherAttempts()
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var command = BuildCommand();
        var callCount = 0;
        DadFrenRiderCommandResult Invoke(string _)
        {
            callCount++;
            if (callCount == 1)
                throw new InvalidOperationException("provider absent");
            return DadFrenRiderCommandResult.Success();
        }

        Assert.Equal(
            DadParticipantFrenRiderHandoffStatus.PendingRetry,
            gate.Apply(command, true, true, Start, Invoke, out var pendingSummary));
        Assert.Contains("InvalidOperationException", pendingSummary, StringComparison.Ordinal);
        Assert.Equal(
            DadParticipantFrenRiderHandoffStatus.Configured,
            gate.Apply(command, true, true, Start.AddSeconds(1), Invoke, out _));
        Assert.Equal(
            DadParticipantFrenRiderHandoffStatus.AlreadyConfigured,
            gate.Apply(command, true, true, Start.AddSeconds(2), Invoke, out _));
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void RetryUsesFrozenTargetAndResetAllowsNewRun()
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var command = BuildCommand();
        var calls = new List<string>();

        gate.Apply(
            command,
            true,
            true,
            Start,
            target =>
            {
                calls.Add(target);
                return DadFrenRiderCommandResult.Failure("retry");
            },
            out _);

        var targetRow = command.Participants.Single(static participant => participant.AssignedSlotId == "Slot1");
        targetRow.ActiveCharacterKey = new DadCharacterKey("Mutated Target@Excalibur");
        targetRow.Character.CharacterKey = "Mutated Target@Excalibur";
        gate.Apply(
            command,
            true,
            true,
            Start.AddSeconds(1),
            target =>
            {
                calls.Add(target);
                return DadFrenRiderCommandResult.Success();
            },
            out _);

        Assert.Equal([WCharacter, WCharacter], calls);

        gate.Reset();
        var newRun = BuildCommand("run-sastasha-2");
        var afterReset = gate.Apply(
            newRun,
            true,
            true,
            Start.AddSeconds(2),
            target =>
            {
                calls.Add(target);
                return DadFrenRiderCommandResult.Success();
            },
            out _);

        Assert.Equal(DadParticipantFrenRiderHandoffStatus.Configured, afterReset);
        Assert.Equal(3, calls.Count);
    }

    private static void AssertTargetRejected(DadWorkerExecutionCommand command)
    {
        var gate = new DadParticipantFrenRiderHandoffGate();
        var callCount = 0;
        var result = gate.Apply(
            command,
            true,
            true,
            Start,
            _ =>
            {
                callCount++;
                return DadFrenRiderCommandResult.Success();
            },
            out _);

        Assert.Equal(DadParticipantFrenRiderHandoffStatus.Failed, result);
        Assert.Equal(0, callCount);
    }

    private static DadWorkerExecutionCommand BuildCommand(string runId = RunId)
    {
        var orchestration = new DadOrchestrationIntent
        {
            AuthorityMode = DadAuthorityMode.ServerDad,
            ModuleTarget = DadModuleId.PremadeDuty,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            PreferredLeaderCharacterKey = new DadCharacterKey(WCharacter),
            PreferredInviterCharacterKey = new DadCharacterKey(WCharacter),
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = 2,
                RequireRemoteParticipants = true,
                AllowStoredXadbFallback = false,
                RequireExactCharacters = true,
            },
            RequiredRosterCharacters =
            [
                RosterReference(WAccount, WCharacter, WContentId),
                RosterReference(XAccount, XCharacter, XContentId),
            ],
            RequiredAccountKeys = [new DadAccountKey(WAccount), new DadAccountKey(XAccount)],
            RequiredCharacterKeys = [new DadCharacterKey(WCharacter), new DadCharacterKey(XCharacter)],
        };
        var request = new DadRunRequest
        {
            RequestId = runId,
            RequestedBy = "participant-frenrider-tests",
            Orchestration = orchestration,
            PremadeDuty = new DadPremadeDutyTask
            {
                ContentFinderConditionId = 4,
                DutyName = "Sastasha",
                Unsynced = true,
                ExpectedPartySize = 2,
                Attempts = 1,
            },
        };
        var module = new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = "Premade Duty",
            OwnerLabel = "DAD",
            ExpectedPartySize = 2,
            RequiresPeers = true,
            Summary = "Premade Sastasha #4",
        };

        return new DadWorkerExecutionCommand
        {
            CommandId = $"command-x-{runId}",
            RunId = runId,
            ModuleIndex = 0,
            Role = DadWorkerExecutionRole.Participant,
            Plan = new DadRunPlan
            {
                Request = request,
                CompositeModuleId = DadModuleId.PremadeDuty,
                Orchestration = orchestration,
                Summary = module.Summary,
                RequiredParticipantCount = 2,
                RequiresRemoteParticipants = true,
                LeaderCharacterKey = WCharacter,
                InviterCharacterKey = WCharacter,
                Modules = [module],
            },
            Participants =
            [
                Assignment(runId, WWorker, WAccount, WCharacter, WContentId, "Slot1", isLocal: false, isAuthority: true),
                Assignment(runId, XWorker, XAccount, XCharacter, XContentId, "Slot2", isLocal: true, isAuthority: false),
            ],
        };
    }

    private static DadRosterCharacterRef RosterReference(string account, string character, ulong contentId)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = new DadCharacterKey(character),
            ContentId = contentId,
        };

    private static DadParticipantSnapshot Assignment(
        string runId,
        string worker,
        string account,
        string character,
        ulong contentId,
        string slot,
        bool isLocal,
        bool isAuthority)
        => new()
        {
            RunId = runId,
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
            Character = new DadAcquiredCharacter
            {
                AccountId = account,
                CharacterKey = character,
                ContentId = contentId,
                Source = isLocal ? DadCharacterSource.LocalRuntime : DadCharacterSource.PeerRuntime,
                Freshness = DadSnapshotFreshness.Live,
                Readiness = DadReadinessState.Ready,
            },
            AssignedSlotId = slot,
            IsLocalClient = isLocal,
            IsAuthority = isAuthority,
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = true,
            State = DadParticipantState.Ready,
        };
}
