using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadWorkerPrequeueBarrierRulesTests
{
    [Fact]
    public void NonLeadersDispatchFirstAndAcceptedIsNotReadiness()
    {
        var (plan, module, participants) = Scenario();
        var statuses = new Dictionary<string, DadWorkerExecutionStatus>(StringComparer.OrdinalIgnoreCase);

        Assert.True(DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
            plan, module, participants, statuses, out var first, out var blocker), blocker);
        Assert.Equal(["worker-x", "worker-t", "worker-y"], first.Select(Worker).ToArray());

        foreach (var participant in first)
            statuses[Worker(participant)] = Status(participant, DadWorkerExecutionState.Accepted);

        Assert.True(DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
            plan, module, participants, statuses, out var acceptedOnly, out blocker), blocker);
        Assert.Empty(acceptedOnly);
        Assert.False(DadWorkerPrequeueBarrierRules.AreAllNonLeadersWaiting(plan, module, participants, statuses));
    }

    [Fact]
    public void LeaderDispatchesExactlyOnceOnlyAfterEveryParticipantWaitsForQueue()
    {
        var (plan, module, participants) = Scenario();
        var statuses = participants
            .Where(participant => Worker(participant) != "worker-w")
            .ToDictionary(
                Worker,
                participant => Status(participant, DadWorkerExecutionState.WaitingForQueue),
                StringComparer.OrdinalIgnoreCase);
        statuses["worker-y"].State = DadWorkerExecutionState.Accepted;

        Assert.True(DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
            plan, module, participants, statuses, out var notReady, out var blocker), blocker);
        Assert.Empty(notReady);

        statuses["worker-y"].State = DadWorkerExecutionState.WaitingForQueue;
        Assert.True(DadWorkerPrequeueBarrierRules.AreAllNonLeadersWaiting(plan, module, participants, statuses));
        Assert.True(DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
            plan, module, participants, statuses, out var leaderDispatch, out blocker), blocker);
        var leader = Assert.Single(leaderDispatch);
        Assert.Equal("worker-w", Worker(leader));

        statuses["worker-w"] = Status(leader, DadWorkerExecutionState.Accepted, DadWorkerExecutionRole.QueueLeader);
        Assert.True(DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
            plan, module, participants, statuses, out var afterDispatch, out blocker), blocker);
        Assert.Empty(afterDispatch);
    }

    [Theory]
    [InlineData(DadWorkerExecutionState.Accepted)]
    [InlineData(DadWorkerExecutionState.Preparing)]
    [InlineData(DadWorkerExecutionState.Repairing)]
    public void PreparationStatesNeverReleaseQueueLeader(DadWorkerExecutionState preparationState)
    {
        var (plan, module, participants) = Scenario();
        var statuses = participants
            .Where(participant => Worker(participant) != "worker-w")
            .ToDictionary(
                Worker,
                participant => Status(participant, DadWorkerExecutionState.WaitingForQueue),
                StringComparer.OrdinalIgnoreCase);
        statuses["worker-y"].State = preparationState;

        Assert.False(DadWorkerPrequeueBarrierRules.AreAllNonLeadersWaiting(plan, module, participants, statuses));
        Assert.True(DadWorkerPrequeueBarrierRules.TryResolveDispatchTargets(
            plan, module, participants, statuses, out var targets, out var blocker), blocker);
        Assert.Empty(targets);
    }

    [Fact]
    public void FailureNamesExactSlotCharacterAndWorker()
    {
        var (_, _, participants) = Scenario();
        var participant = participants.Single(row => Worker(row) == "worker-t");

        var failure = DadWorkerPrequeueBarrierRules.AttributeFailure(
            participant,
            "ADS.PatchConfigurationJson rejected the required configuration patch.");

        Assert.Contains("slot 'Slot3'", failure, StringComparison.Ordinal);
        Assert.Contains("character 'T Character@World'", failure, StringComparison.Ordinal);
        Assert.Contains("worker 'worker-t'", failure, StringComparison.Ordinal);
        Assert.Contains("PatchConfigurationJson", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void PrequeueFailureCancelsOnlyAcknowledgedWorkers()
    {
        var (_, _, participants) = Scenario();

        var scope = DadWorkerPrequeueBarrierRules.ResolveCancellationScope(
            participants,
            ["worker-x", "worker-t"]);

        Assert.Equal(["worker-x", "worker-t"], scope.Select(Worker).ToArray());
        Assert.DoesNotContain(scope, participant => Worker(participant) == "worker-w");
        Assert.DoesNotContain(scope, participant => Worker(participant) == "worker-y");
    }

    private static (DadRunPlan Plan, DadPlannedModuleExecution Module, List<DadParticipantSnapshot> Participants) Scenario()
    {
        var module = new DadPlannedModuleExecution
        {
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = "Four-player duty",
            ExpectedPartySize = 4,
            RequiresPeers = true,
        };
        var plan = new DadRunPlan
        {
            Request = new DadRunRequest { RequestId = "run", RequestedBy = "direct-or-scheduler" },
            RequiredParticipantCount = 4,
            LeaderCharacterKey = "W Character@World",
            Modules = [module],
        };
        return (plan, module,
        [
            Participant("worker-w", "Slot1", "W Character@World", local: true),
            Participant("worker-x", "Slot2", "X Character@World"),
            Participant("worker-t", "Slot3", "T Character@World"),
            Participant("worker-y", "Slot4", "Y Character@World"),
        ]);
    }

    private static DadParticipantSnapshot Participant(
        string worker,
        string slot,
        string character,
        bool local = false)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            AssignedSlotId = slot,
            ActiveCharacterKey = new DadCharacterKey(character),
            Character = new DadAcquiredCharacter { CharacterKey = character },
            IsLocalClient = local,
        };

    private static DadWorkerExecutionStatus Status(
        DadParticipantSnapshot participant,
        DadWorkerExecutionState state,
        DadWorkerExecutionRole role = DadWorkerExecutionRole.Participant)
        => new()
        {
            RunId = "run",
            WorkerSessionId = participant.WorkerSessionId,
            Role = role,
            State = state,
        };

    private static string Worker(DadParticipantSnapshot participant)
        => participant.WorkerSessionId.Value;
}
