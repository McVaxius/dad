extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using Presence = DadRuntime::dad.Services.DadPresenceService;
using Coordinator = DadRuntime::dad.Services.DadCoordinatorService;
using Transport = DadRuntime::dad.Services.DadTransportService;

namespace dad.Tests;

[Collection("DAD retry clock boundary")]
public sealed class DadTeardownIdentityRecoveryRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedMutationTimeoutRemainsPendingAndRetriesAfterBackoff(bool leader)
    {
        using var f = new Fixture(leader);
        f.JoinNativeParty();
        for (var i = 0; i < 7; i++)
        {
            if (i > 0) f.Clock.Now += TimeSpan.FromSeconds(8);
            Assert.True(f.Pulse().Deferred);
            f.ClearNativePrompt(); // Native action had no effect; never fake success.
        }
        Assert.Equal(7, f.Actions.Count);
        f.Clock.Now += TimeSpan.FromSeconds(12);
        var waiting = f.Pulse();
        Assert.True(waiting.Deferred, waiting.Summary);
        Assert.False(waiting.TimedOut);
        Assert.Empty(waiting.FailureReason);
        Assert.Equal(M.DadParticipantState.AssemblyPending, waiting.ParticipantState);
        f.Clock.Now += TimeSpan.FromSeconds(29);
        Assert.True(f.Pulse().Deferred);
        Assert.Equal(7, f.Actions.Count);
        f.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(f.Pulse().Deferred);
        Assert.Equal(8, f.Actions.Count);
        f.LeaveNativeParty();
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(f.Pulse().Success);
    }

    [Fact]
    public void FollowerTimeoutWhileLeaderRouteIsMissingDoesNotPoisonCoordinatorResult()
    {
        using var f = new Fixture(false);
        f.JoinNativeParty();
        var coordinator = CoordinatorFor(f);
        ((List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!)
            .RemoveAll(row => row.AssignedSlotId == "Slot1");
        for (var i = 0; i < 7; i++)
        {
            if (i > 0) f.Clock.Now += TimeSpan.FromSeconds(8);
            coordinator.Update();
            f.ClearNativePrompt();
        }
        f.Clock.Now += TimeSpan.FromSeconds(12);
        coordinator.Update();
        var terminal = (Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!;
        Assert.Empty(terminal);
        Assert.Equal(M.DadRunStatus.Running, coordinator.CurrentResult.Status);
        Assert.Equal(M.DadParticipantState.AssemblyPending, f.Presence.CurrentParticipant.State);
        Assert.Empty(coordinator.CurrentResult.FailureReason);
        terminal["Slot1"] = new() { RunId = "run", Success = true, Summary = "Original leader reconnected and disbanded." };
        f.LeaveNativeParty();
        coordinator.Update();
        f.Clock.Now += TimeSpan.FromSeconds(1);
        coordinator.Update();
        Assert.Equal(M.DadRunPhase.Finalizing, coordinator.CurrentResult.Phase);
        Assert.True((bool)Get(coordinator, "successfulPartyBoundarySettled")!);
        Assert.Equal(7, f.Actions.Count);
    }

    [Fact]
    public void RacedNativeMutationBackoffDoesNotClickAgainAndStopClearsRetry()
    {
        using var f = new Fixture(true);
        f.FailNextNativeAction = true;
        var pending = f.Pulse();
        Assert.True(pending.Deferred);
        Assert.Empty(pending.FailureReason);
        Assert.Single(f.Actions);
        for (var i = 0; i < 5; i++)
        {
            f.Clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(f.Pulse().Deferred);
        }
        Assert.Single(f.Actions);
        Assert.True(f.Presence.HandleCancelRun(new() { RunId = "run", Reason = "stop" }).Acknowledged);
        Assert.Null(Get(f.Presence, "partyTeardownRetryAtUtc"));
        f.Clock.Now += TimeSpan.FromHours(2);
        Assert.False(f.Pulse().Deferred);
        Assert.Single(f.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetryBackoffNeverSuppressesFrozenPayloadOrNativeIdentityContradictions(bool payloadDrift)
    {
        using var f = new Fixture(true);
        f.FailNextNativeAction = true;
        Assert.True(f.Pulse().Deferred);
        if (payloadDrift) f.Instruction.Summary = "changed while retrying";
        else f.SetNativeForeignMember();
        var failed = f.Pulse();
        Assert.False(failed.Deferred);
        Assert.False(failed.Success);
        Assert.NotEmpty(failed.FailureReason);
        Assert.Single(f.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinatorCleansReachableMemberWhileOtherRouteRemainsPending(bool localLeader)
    {
        using var f = new Fixture(localLeader);
        var coordinator = CoordinatorFor(f);
        var localSlot = localLeader ? "Slot1" : "Slot2";
        var missingSlot = localLeader ? "Slot2" : "Slot1";
        var rows = (List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!;
        rows.RemoveAll(row => row.AssignedSlotId == missingSlot);
        // This is the real coordinator dispatch/aggregate boundary, with the
        // real local presence/controller and a synthetic native party adapter.
        for (var tick = 0; tick < 5; tick++)
        {
            coordinator.Update();
            f.Clock.Now += TimeSpan.FromSeconds(1);
        }
        var terminal = (Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!;
        Assert.True(terminal[localSlot].Success);
        Assert.False(terminal.ContainsKey(missingSlot));
        var actionCount = f.Actions.Count;
        f.Clock.Now += TimeSpan.FromHours(2);
        coordinator.Update();
        Assert.Equal(M.DadRunPhase.TearingDownParty, coordinator.CurrentResult.Phase);
        Assert.Equal(M.DadRunStatus.Running, coordinator.CurrentResult.Status);
        Assert.Empty(coordinator.CurrentResult.FailureReason);
        Assert.Equal(actionCount, f.Actions.Count); // Completed clients are not replayed.
        Assert.False((bool)Get(coordinator, "successfulPartyBoundarySettled")!);
        // Correlated terminal proof from the returning worker releases the
        // existing successful run to finalization, not back to queue/formation.
        terminal[missingSlot] = new() { RunId = "run", Success = true, Summary = "Exact returning worker is solo." };
        coordinator.Update();
        Assert.Equal(M.DadRunPhase.Finalizing, coordinator.CurrentResult.Phase);
        Assert.True((bool)Get(coordinator, "successfulPartyBoundarySettled")!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinatorCanStartIndependentTeardownWithoutFreshAllOnlineBarrier(bool localLeader)
    {
        using var f = new Fixture(localLeader);
        var coordinator = CoordinatorFor(f);
        var localSlot = localLeader ? "Slot1" : "Slot2";
        var missingSlot = localLeader ? "Slot2" : "Slot1";
        var rows = (List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!;
        rows.RemoveAll(row => row.AssignedSlotId == missingSlot);
        rows.Single().PostArReady = false;
        // Enter through the actual builder after a route has disappeared.
        // Discard CoordinatorFor's pre-populated teardown instructions first.
        Set(coordinator, "activePartyTeardownInstructions", new List<M.DadAssemblyInstructionDto>());
        f.HideIdentity(); // Accepted frozen roster remains; live coordinator is at login.
        Invoke(coordinator, "BeginPartyTeardown", "Duty already completed.");
        Assert.Equal(M.DadRunPhase.TearingDownParty, coordinator.CurrentResult.Phase);
        var instructions = (List<M.DadAssemblyInstructionDto>)Get(coordinator, "activePartyTeardownInstructions")!;
        Assert.Equal(2, instructions.Count);
        Assert.Equal("Leader@World", instructions[0].RequiredCharacterKey.Value);
        Assert.Equal("Follower@World", instructions[1].RequiredCharacterKey.Value);
        Assert.All(instructions, instruction =>
        {
            Assert.Equal(1UL, instruction.FrozenInviter.ContentId);
            Assert.Equal(2UL, Assert.Single(instruction.InviteTargets).ContentId);
            Assert.Equal(f.Presence.WorkerSessionId, instruction.AuthorityWorkerSessionId);
        });
        coordinator.Update();
        f.Clock.Now += TimeSpan.FromHours(2);
        coordinator.Update();
        Assert.False(coordinator.CurrentResult.IsTerminal);
        Assert.Equal(M.DadRunPhase.TearingDownParty, coordinator.CurrentResult.Phase);
        Assert.Empty(coordinator.CurrentResult.FailureReason);
        Assert.Empty(f.Actions);
        Assert.Empty((Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!);

        f.RestoreIdentity();
        for (var tick = 0; tick < 5; tick++)
        {
            coordinator.Update();
            f.Clock.Now += TimeSpan.FromSeconds(1);
        }
        var terminal = (Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!;
        Assert.True(terminal[localSlot].Success);
        Assert.False(terminal.ContainsKey(missingSlot));
        Assert.Equal(M.DadRunPhase.TearingDownParty, coordinator.CurrentResult.Phase);
        Assert.False((bool)Get(coordinator, "successfulPartyBoundarySettled")!);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("slot")]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("content")]
    [InlineData("stored-content")]
    [InlineData("foreign-party")]
    public void TeardownBuiltWhileRouteIsMissingStillRejectsFreshContradictions(string contradiction)
    {
        using var f = new Fixture(true);
        var coordinator = CoordinatorFor(f);
        ((List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!)
            .RemoveAll(row => row.AssignedSlotId == "Slot2");
        Set(coordinator, "activePartyTeardownInstructions", new List<M.DadAssemblyInstructionDto>());
        Invoke(coordinator, "BeginPartyTeardown", "Duty already completed.");
        var instructions = (List<M.DadAssemblyInstructionDto>)Get(coordinator, "activePartyTeardownInstructions")!;
        Assert.Equal(2, instructions.Count);
        var leader = instructions.Single(instruction => instruction.SlotId == "Slot1");
        switch (contradiction)
        {
            case "authority": leader.AuthorityWorkerSessionId = new("foreign"); break;
            case "slot": Set(f.Presence, "assignedSlotId", "Slot3"); break;
            case "account": f.Presence.CurrentParticipant.ManagedAccountKey = new("foreign"); break;
            case "character": f.Presence.CurrentParticipant.ActiveCharacterKey = new("Foreign@World"); break;
            case "content": f.Presence.CurrentParticipant.Character.ContentId = 99; break;
            case "stored-content": Set(f.Presence, "requiredContentId", 99UL); break;
            case "foreign-party": f.SetNativeForeignMember(); break;
        }
        coordinator.Update();
        var terminal = (Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!;
        Assert.False(terminal["Slot1"].Success);
        Assert.False(terminal["Slot1"].Deferred);
        Assert.NotEmpty(terminal["Slot1"].FailureReason);
        Assert.False(terminal.ContainsKey("Slot2"));
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void CompleteFollowerIdentityRejectsAChangedInviteTargetSlotBeforeLeaving()
    {
        using var f = new Fixture(false);
        f.JoinNativeParty();
        var coordinator = CoordinatorFor(f);
        ((List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!)
            .RemoveAll(row => row.AssignedSlotId == "Slot1");
        Set(coordinator, "activePartyTeardownInstructions", new List<M.DadAssemblyInstructionDto>());
        Invoke(coordinator, "BeginPartyTeardown", "Duty already completed.");
        var instructions = (List<M.DadAssemblyInstructionDto>)Get(coordinator, "activePartyTeardownInstructions")!;
        var instruction = instructions.Single(candidate => candidate.SlotId == "Slot2");
        var target = Assert.Single(instruction.InviteTargets);
        Assert.Equal("Slot2", target.SlotId);
        Assert.Equal("Slot2", f.Presence.CurrentParticipant.AssignedSlotId);
        Assert.Equal("Follower@World", f.Presence.CurrentParticipant.ActiveCharacterKey.Value);
        Assert.Equal(2UL, f.Presence.CurrentParticipant.Character.ContentId);
        Assert.Null(Get(f.Presence, "partyTeardownIdentityWaitStartedUtc"));
        typeof(M.DadNativePartyInviteTarget).GetProperty(nameof(M.DadNativePartyInviteTarget.SlotId))!
            .SetValue(target, "Slot3");

        var failed = f.Presence.HandleAssemblyInstruction(instruction);
        Assert.False(failed.Success);
        Assert.False(failed.Deferred);
        Assert.Contains("frozen Slot1 invite target", failed.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("account")]
    [InlineData("content")]
    [InlineData("worker")]
    [InlineData("duplicate-worker")]
    [InlineData("leader")]
    [InlineData("inviter")]
    public void RouteIndependentTeardownRequiresAnExactAcceptedFrozenManifest(string contradiction)
    {
        using var f = new Fixture(true);
        var coordinator = CoordinatorFor(f);
        var manifest = Get(coordinator, "activeSlotManifest")!;
        var slots = (IList)manifest.GetType().GetProperty("Slots")!.GetValue(manifest)!;
        var leader = slots[0]!;
        void Property(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
        switch (contradiction)
        {
            case "run": Property(manifest, "RequestId", "foreign-run"); break;
            case "account": Property(leader, "AccountKey", new M.DadAccountKey("")); break;
            case "content": Property(leader, "ContentId", 0UL); break;
            case "worker": Property(leader, "WorkerSessionId", new M.DadWorkerSessionId("")); break;
            case "duplicate-worker": Property(slots[1]!, "WorkerSessionId", new M.DadWorkerSessionId("worker-1")); break;
            case "leader": Property(leader, "IsLeader", false); break;
            case "inviter": Property(leader, "IsInviter", false); break;
        }
        var builder = new DadRuntime::dad.Services.DadPartyAssemblyService();
        var method = builder.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "BuildTeardownInstructions" && candidate.GetParameters().Length == 6);
        object?[] arguments = [Get(coordinator, "activePlan"), Array.Empty<M.DadParticipantSnapshot>(),
            manifest, f.Presence.WorkerSessionId, null, null];
        var instructions = (List<M.DadAssemblyInstructionDto>)method.Invoke(builder, arguments)!;
        Assert.Empty(instructions);
        Assert.NotEmpty((string)arguments[^1]!);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void FrozenCleanupIdentityCannotAuthorizePartyFormation()
    {
        using var f = new Fixture(true);
        var coordinator = CoordinatorFor(f);
        ((List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!).Clear();
        Set(coordinator, "activePartyTeardownInstructions", new List<M.DadAssemblyInstructionDto>());
        Invoke(coordinator, "BeginPartyTeardown", "Duty already completed.");
        var instructions = (List<M.DadAssemblyInstructionDto>)Get(coordinator, "activePartyTeardownInstructions")!;
        Assert.Equal(2, instructions.Count);
        var instruction = instructions[0].Clone();
        Assert.Equal((ushort)0, instruction.FrozenInviter.WorldId);
        instruction.InstructionKind = M.DadAssemblyInstructionKind.FormParty;
        var failed = f.Presence.HandleAssemblyInstruction(instruction);
        Assert.False(failed.Success);
        Assert.False(failed.Deferred);
        Assert.Contains("World ID", failed.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void RuntimeDisconnectAfterMutationPausesTheExistingController()
    {
        using var f = new Fixture(true);
        Assert.True(f.Pulse().Deferred); // First disband command; no approval yet.
        Assert.Single(f.Actions);
        f.Clock.Now += TimeSpan.FromSeconds(20);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromHours(2);
        Assert.True(f.Pulse().Deferred);
        Assert.Single(f.Actions);
        f.RestoreIdentity();
        Assert.True(f.Pulse().Deferred); // Old visible prompt must not be approved.
        Assert.Single(f.Actions);
        f.ClearNativePrompt();
        Assert.True(f.Pulse().Deferred); // Retry using the original attempt budget.
        f.Pulse(); // Fresh owned prompt.
        f.Pulse(); // First authoritative solo observation.
        f.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(f.Pulse().Success);
        Assert.Equal(3, f.Actions.Count);
    }

    [Fact]
    public void UnreadableRouteIdentityIsPendingButAnExplicitContradictionStillFailsClosed()
    {
        using var f = new Fixture(true);
        var coordinator = CoordinatorFor(f);
        var rows = (List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!;
        var missing = rows.Single(row => row.AssignedSlotId == "Slot2");
        missing.ActiveCharacterKey = new("");
        missing.Character.ContentId = 0;
        missing.ManagedAccountKey = new("");
        coordinator.Update();
        var terminal = (Dictionary<string, M.DadRunStepResultDto>)Get(coordinator, "partyTeardownTerminalResults")!;
        Assert.False(terminal.ContainsKey("Slot2"));
        missing.ManagedAccountKey = new("foreign-account");
        coordinator.Update();
        Assert.False(terminal["Slot2"].Success);
        Assert.NotEmpty(terminal["Slot2"].FailureReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongLoginQueueRemainsPendingAndExactRestorationCanFinish(bool leader)
    {
        using var f = new Fixture(leader);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromHours(2);
        var waiting = f.Pulse();
        Assert.True(waiting.Deferred, waiting.Summary);
        Assert.False(waiting.TimedOut);
        Assert.Empty(waiting.FailureReason);
        Assert.Empty(f.Actions);
        f.RestoreIdentity();
        var result = f.Pulse();
        if (leader)
        {
            Assert.True(result.Deferred, result.Summary);
            f.Pulse();
            f.Pulse();
            f.Clock.Now += TimeSpan.FromSeconds(1);
            result = f.Pulse();
        }
        Assert.True(result.Success, result.Summary);
        Assert.False(result.Deferred);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIdentityDefersWithoutNativeActionsAndExactRestorationCompletes(bool leader)
    {
        using var f = new Fixture(leader);
        f.HideIdentity();
        var waiting = f.Pulse();
        Assert.True(waiting.Deferred, waiting.Summary);
        Assert.False(waiting.Success);
        Assert.Empty(waiting.FailureReason);
        Assert.Empty(waiting.AuthoritativePartyMembers);
        Assert.Empty(f.Actions);
        f.Clock.Now += TimeSpan.FromSeconds(3);
        f.RestoreIdentity();
        var complete = f.Pulse();
        if (leader)
        {
            Assert.True(complete.Deferred, complete.Summary);
            Assert.Single(f.Actions);
            f.Pulse(); // Approve only the new, owned disband confirmation.
            f.Pulse(); // Start sustained solo confirmation.
            f.Clock.Now += TimeSpan.FromSeconds(1);
            complete = f.Pulse();
            Assert.Equal(2, f.Actions.Count);
        }
        Assert.True(complete.Success, complete.Summary);
        Assert.False(complete.Deferred);
        if (!leader) Assert.Empty(f.Actions); // Native source already proves follower solo.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialIdentityAlsoWaitsButDoesNotCountAsTerminalTeardown(bool missingCharacter)
    {
        using var f = new Fixture(false);
        if (missingCharacter) f.Presence.CurrentParticipant.ActiveCharacterKey = new("");
        else f.Presence.CurrentParticipant.Character.ContentId = 0;
        var result = f.Pulse();
        Assert.True(result.Deferred, result.Summary);
        Assert.Empty(f.Actions);
        var terminal = new Dictionary<string, M.DadRunStepResultDto>();
        if (!result.Deferred) terminal["Slot2"] = result;
        Assert.False(DadRuntime::dad.Services.DadExactPartyTeardownRules.Aggregate([f.Instruction], terminal).Complete);
    }

    [Fact]
    public void ChangedTeardownPayloadCannotRefreshOrReuseTheWait()
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Instruction.Summary = "changed payload";
        var result = f.Pulse();
        Assert.False(result.Deferred);
        Assert.Contains("changed", result.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void CancellationClearsPendingWaitAndOldRunCannotResume()
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromHours(2);
        Assert.True(f.Pulse().Deferred);
        var ack = f.Presence.HandleCancelRun(new() { RunId = "run", Reason = "operator cancellation" });
        Assert.True(ack.Acknowledged);
        f.RestoreIdentity();
        var result = f.Pulse();
        Assert.False(result.Deferred);
        Assert.False(result.Success);
        Assert.Contains("different run", result.FailureReason);
        Assert.Null(typeof(Presence).GetField("partyTeardownIdentityWaitStartedUtc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Presence));
        Assert.Empty(f.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedMissingIdentityDoesNotBecomeATerminalFailure(bool restoreAfterWait)
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromSeconds(59);
        Assert.True(f.Pulse().Deferred);
        f.Clock.Now += TimeSpan.FromHours(2);
        if (restoreAfterWait) f.RestoreIdentity();
        var result = f.Pulse();
        Assert.Equal(!restoreAfterWait, result.Deferred);
        Assert.Equal(restoreAfterWait, result.Success);
        Assert.False(result.TimedOut, result.Summary);
        Assert.Empty(result.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("worker")]
    [InlineData("slot")]
    [InlineData("authority")]
    [InlineData("stored-character")]
    [InlineData("stored-content")]
    [InlineData("observed-character")]
    [InlineData("observed-content")]
    [InlineData("run")]
    public void ContradictionsNeverBecomeIdentityWaits(string contradiction)
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        switch (contradiction)
        {
            case "account": f.Presence.CurrentParticipant.ManagedAccountKey = new("foreign"); break;
            case "worker": Set(f.Presence, "<WorkerSessionId>k__BackingField", new M.DadWorkerSessionId("foreign")); break;
            case "slot": Set(f.Presence, "assignedSlotId", "Slot3"); break;
            case "authority": Set(f.Presence, "currentAuthorityWorkerSessionId", new M.DadWorkerSessionId("foreign")); break;
            case "stored-character": Set(f.Presence, "requiredCharacterKey", new M.DadCharacterKey("Foreign@World")); break;
            case "stored-content": Set(f.Presence, "requiredContentId", 99UL); break;
            case "observed-character": f.Presence.CurrentParticipant.ActiveCharacterKey = new("Foreign@World"); break;
            case "observed-content": f.Presence.CurrentParticipant.Character.ContentId = 99; break;
            case "run": Set(f.Presence, "currentRunId", "other-run"); break;
        }
        var failed = f.Pulse();
        Assert.False(failed.Success);
        Assert.False(failed.Deferred);
        Assert.NotEmpty(failed.FailureReason);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public void OrdinaryJoinStillFailsWhenItsCharacterIsUnavailable()
    {
        using var f = new Fixture(false);
        f.HideIdentity();
        f.Instruction.InstructionKind = M.DadAssemblyInstructionKind.JoinParty;
        var result = f.Pulse();
        Assert.False(result.Deferred);
        Assert.False(result.Success);
        Assert.NotEmpty(result.FailureReason);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PropertyInfo clockProperty;
        private readonly object? previousClock;
        private readonly ulong content;
        private readonly string character;
        private readonly object native;
        public Presence Presence { get; }
        public M.DadAssemblyInstructionDto Instruction { get; }
        public ManualClock Clock { get; } = new();
        public List<string> Actions { get; } = [];
        public bool FailNextNativeAction { get; set; }

        public Fixture(bool leader)
        {
            clockProperty = typeof(Presence).Assembly.GetType("dad.DadClock")!
                .GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Static)!;
            previousClock = clockProperty.GetValue(null);
            clockProperty.SetValue(null, Clock);
            content = leader ? 1UL : 2UL;
            character = leader ? "Leader@World" : "Follower@World";
            var worker = new M.DadWorkerSessionId(leader ? "worker-1" : "worker-2");
            var account = new M.DadAccountKey(leader ? "account-1" : "account-2");
            var config = new DadRuntime::dad.Configuration();
            var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
            var player = DispatchProxy.Create<IPlayerState, ServiceProxy>();
            ((ServiceProxy)(object)player).Handler = method => method.Name == "get_ContentId" ? content : null;
            var condition = DispatchProxy.Create<ICondition, ServiceProxy>();
            ((ServiceProxy)(object)condition).Handler = _ => false;
            var framework = DispatchProxy.Create<IFramework, ServiceProxy>();
            ((ServiceProxy)(object)framework).Handler = _ => true;
            native = New("dad.Headless.VirtualPartyGateway", content, (Action<string>)(action =>
            {
                Actions.Add(action);
                if (!FailNextNativeAction) return;
                FailNextNativeAction = false;
                throw new InvalidOperationException("Synthetic native UI changed before mutation.");
            }));
            if (leader)
                ((List<M.DadPartyMemberSnapshot>)native.GetType().GetProperty("Members")!.GetValue(native)!)
                    .Add(new() { ContentId = 2 });
            var teardown = New("dad.Services.DadPartyTeardownService", config, null!, player, condition, log, native);
            var gateway = New("dad.Services.InfoProxyPartyInviteGateway", config, framework, player, null!, condition, log, native);
            var ctor = typeof(Presence).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            Presence = (Presence)ctor.Invoke(ctor.GetParameters().Select(p => p.Name switch
            {
                "configuration" => (object)config,
                "partyInviteGateway" => gateway,
                "partyTeardownService" => teardown,
                "requestedJobPreparationGate" => Activator.CreateInstance(p.ParameterType),
                "log" => log,
                _ => null,
            }).ToArray());
            Set(Presence, "<WorkerSessionId>k__BackingField", worker);
            Set(Presence, "currentRunId", "run");
            Set(Presence, "currentAuthorityWorkerSessionId", new M.DadWorkerSessionId("worker-1"));
            Set(Presence, "requiredAccountKey", account);
            Set(Presence, "requiredCharacterKey", new M.DadCharacterKey(character));
            Set(Presence, "requiredContentId", content);
            Set(Presence, "assignedSlotId", leader ? "Slot1" : "Slot2");
            Presence.CurrentParticipant.ManagedAccountKey = account;
            Presence.CurrentParticipant.WorkerSessionId = worker;
            Presence.CurrentParticipant.AssignedSlotId = leader ? "Slot1" : "Slot2";
            Presence.CurrentParticipant.IsLocalClient = true;
            Presence.CurrentParticipant.PostArReady = true;
            Presence.CurrentParticipant.Character.CharacterKey = character;
            Presence.CurrentParticipant.Character.CharacterName = leader ? "Leader" : "Follower";
            Presence.CurrentParticipant.Character.WorldId = 1;
            Instruction = new()
            {
                RunId = "run", ModuleId = M.DadModuleId.DailyMsq,
                SlotId = leader ? "Slot1" : "Slot2", RequiredCharacterKey = new(character),
                AuthorityWorkerSessionId = new("worker-1"),
                InstructionKind = leader ? M.DadAssemblyInstructionKind.DisbandParty : M.DadAssemblyInstructionKind.LeaveParty,
                FrozenInviter = new()
                {
                    RunId = "run", WorkerSessionId = new("worker-1"), AccountKey = new("account-1"),
                    CharacterKey = new("Leader@World"), ContentId = 1, CharacterName = "Leader", WorldId = 1,
                },
                InviteTargets = [new()
                {
                    RunId = "run", ModuleId = M.DadModuleId.DailyMsq, SlotId = "Slot2", WorkerSessionId = new("worker-2"),
                    AccountKey = new("account-2"), CharacterKey = new("Follower@World"),
                    ContentId = 2, CharacterName = "Follower", WorldId = 1,
                }],
            };
            RestoreIdentity();
        }

        public M.DadRunStepResultDto Pulse() => Presence.HandleAssemblyInstruction(Instruction);
        public void ClearNativePrompt()
        {
            var prompt = native.GetType().GetProperty("Prompt")!;
            prompt.SetValue(native, Activator.CreateInstance(prompt.PropertyType));
        }
        public void JoinNativeParty() => native.GetType().GetProperty("Members")!.SetValue(native,
            new List<M.DadPartyMemberSnapshot> { new() { ContentId = 1, IsLocalPlayer = content == 1 }, new() { ContentId = 2, IsLocalPlayer = content == 2 } });
        public void SetNativeForeignMember() => ((List<M.DadPartyMemberSnapshot>)native.GetType().GetProperty("Members")!.GetValue(native)!)
            .Add(new() { ContentId = 99 });
        public void LeaveNativeParty()
        {
            ClearNativePrompt();
            native.GetType().GetProperty("Members")!.SetValue(native,
                new List<M.DadPartyMemberSnapshot> { new() { ContentId = content, IsLocalPlayer = true } });
        }
        public void HideIdentity() { Presence.CurrentParticipant.ActiveCharacterKey = new(""); Presence.CurrentParticipant.Character.ContentId = 0; }
        public void RestoreIdentity() { Presence.CurrentParticipant.ActiveCharacterKey = new(character); Presence.CurrentParticipant.Character.ContentId = content; }
        public void Dispose() => clockProperty.SetValue(null, previousClock);
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler { get; set; } = _ => null;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!);
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static object New(string name, params object[] args)
        => Activator.CreateInstance(typeof(Presence).Assembly.GetType(name)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, args, null)!;
    private static object? Get(object target, string field)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Invoke(object target, string method, params object[] args)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Coordinator CoordinatorFor(Fixture f)
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var transport = (Transport)RuntimeHelpers.GetUninitializedObject(typeof(Transport));
        Set(transport, "<CurrentTransport>k__BackingField", new M.DadPeerTransportSnapshot());
        var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
        var constructor = typeof(Coordinator).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var coordinator = (Coordinator)constructor.Invoke(constructor.GetParameters().Select(p => p.Name switch
        {
            "configuration" => (object)config,
            "presenceService" => f.Presence,
            "transportService" => transport,
            "claimService" => new DadRuntime::dad.Services.DadClaimService(),
            "partyAssemblyService" => new DadRuntime::dad.Services.DadPartyAssemblyService(),
            "log" => log,
            _ => null,
        }).ToArray());
        var request = new M.DadRunRequest { RequestId = "run" };
        Set(f.Presence, "currentAuthorityWorkerSessionId", f.Presence.WorkerSessionId);
        var plan = new M.DadRunPlan
        {
            Request = request, Orchestration = request.Orchestration,
            CompositeModuleId = M.DadModuleId.DailyMsq, RequiredParticipantCount = 2,
            LeaderCharacterKey = "Leader@World", InviterCharacterKey = "Leader@World",
        };
        Set(coordinator, "activePlan", plan);
        typeof(Coordinator).GetProperty("CurrentResult")!.SetValue(coordinator,
            new M.DadRunResult { RequestId = "run", Request = request, Status = M.DadRunStatus.Running, Phase = M.DadRunPhase.TearingDownParty });
        var manifest = New("dad.Models.DadRunSlotManifest");
        void Property(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
        Property(manifest, "RequestId", "run");
        Property(manifest, "ExpectedPartySize", 2);
        Property(manifest, "LeaderCharacterKey", "Leader@World");
        Property(manifest, "InviterCharacterKey", "Leader@World");
        var slots = (IList)manifest.GetType().GetProperty("Slots")!.GetValue(manifest)!;
        var participants = (List<M.DadParticipantSnapshot>)Get(coordinator, "activeParticipants")!;
        var instructions = new List<M.DadAssemblyInstructionDto>();
        for (var i = 1; i <= 2; i++)
        {
            var slot = New("dad.Models.DadFrozenRunSlot");
            var name = i == 1 ? "Leader" : "Follower";
            Property(slot, "SlotId", $"Slot{i}");
            Property(slot, "AccountKey", new M.DadAccountKey($"account-{i}"));
            Property(slot, "CharacterKey", new M.DadCharacterKey($"{name}@World"));
            Property(slot, "ContentId", (ulong)i);
            Property(slot, "WorkerSessionId", new M.DadWorkerSessionId($"worker-{i}"));
            Property(slot, "IsLeader", i == 1);
            Property(slot, "IsInviter", i == 1);
            slots.Add(slot);
            var local = f.Instruction.SlotId == $"Slot{i}";
            participants.Add(local ? f.Presence.CurrentParticipant.Clone() : new()
            {
                WorkerSessionId = new($"worker-{i}"), ManagedAccountKey = new($"account-{i}"),
                AssignedSlotId = $"Slot{i}", ActiveCharacterKey = new($"{name}@World"), PostArReady = true,
                Character = new() { CharacterKey = $"{name}@World", CharacterName = name, WorldId = 1, ContentId = (ulong)i },
            });
            var instruction = f.Instruction.Clone();
            instruction.AuthorityWorkerSessionId = f.Presence.WorkerSessionId;
            instruction.SlotId = $"Slot{i}";
            instruction.RequiredCharacterKey = new($"{name}@World");
            instruction.InstructionKind = i == 1 ? M.DadAssemblyInstructionKind.DisbandParty : M.DadAssemblyInstructionKind.LeaveParty;
            instructions.Add(instruction);
        }
        Set(coordinator, "activeSlotManifest", manifest);
        Set(coordinator, "activePartyTeardownInstructions", instructions);
        return coordinator;
    }
    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
