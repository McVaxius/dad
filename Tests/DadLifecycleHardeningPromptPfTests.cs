using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLifecycleHardeningPromptPfTests
{
    private static readonly DateTime Now =
        new(2026, 8, 2, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PromptApprovalRequiresFreshReadyIdentityFrozenAttemptAndExactText()
    {
        var request = PromptRequest(
            current: new DadPromptObservation(
                true,
                true,
                "prompt-2",
                "Join Expected Leader's party?",
                true));

        var exact = DadPromptOwnershipRules.Evaluate(request);

        Assert.Equal(DadPromptApprovalKind.Exact, exact.Kind);
        Assert.True(exact.CanApprove);
        Assert.False(exact.UsedOverride);
        Assert.False(DadPromptOwnershipRules.Evaluate(
            request with { CurrentOperationKey = "changed" }).CanApprove);
        Assert.False(DadPromptOwnershipRules.Evaluate(
            request with { CurrentAttempt = 2 }).CanApprove);
        Assert.False(DadPromptOwnershipRules.Evaluate(
            request with { ApprovedAttempt = 1 }).CanApprove);
        Assert.False(DadPromptOwnershipRules.Evaluate(
            request with
            {
                Baseline = request.Current,
            }).CanApprove);
    }

    [Fact]
    public void PromptOverrideIsDefaultOffSolePromptOnlyAndExplicitlyWarned()
    {
        var unreadable = PromptRequest(
            current: new DadPromptObservation(
                true,
                true,
                "prompt-2",
                string.Empty,
                true));

        Assert.False(DadPromptOwnershipRules.Evaluate(unreadable).CanApprove);
        Assert.False(DadPromptOwnershipRules.Evaluate(
            unreadable with
            {
                AllowFreshUnprovenPromptApproval = true,
                Current = unreadable.Current with { SoleReadyPrompt = false },
            }).CanApprove);

        var overridden = DadPromptOwnershipRules.Evaluate(
            unreadable with { AllowFreshUnprovenPromptApproval = true });

        Assert.Equal(DadPromptApprovalKind.Override, overridden.Kind);
        Assert.True(overridden.UsedOverride);
        Assert.Contains("WARNING", overridden.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncPfResultRequiresCurrentGenerationAndEveryFrozenIdentityField()
    {
        var instruction = Instruction();
        var result = Result(instruction);

        Assert.True(DadAlliancePartyFinderRules.TryValidateAsyncResult(
            7,
            7,
            instruction,
            result,
            instruction.CoordinatorWorkerSessionId,
            out var blocker), blocker);
        Assert.False(DadAlliancePartyFinderRules.TryValidateAsyncResult(
            6,
            7,
            instruction,
            result,
            instruction.CoordinatorWorkerSessionId,
            out _));

        foreach (var contradicted in ContradictedResults(result))
        {
            Assert.False(DadAlliancePartyFinderRules.TryValidateAsyncResult(
                7,
                7,
                instruction,
                contradicted,
                instruction.CoordinatorWorkerSessionId,
                out _));
        }
    }

    [Fact]
    public void AsyncPfCancellationRequiresExactStopGenerationAndInstructionIdentity()
    {
        var instruction = Instruction();
        var cancellation = new DadAllianceRecruitmentCancellationDto
        {
            RecruitmentId = instruction.RecruitmentId,
            CoordinatorWorkerSessionId = instruction.CoordinatorWorkerSessionId,
            TargetWorkerSessionId = instruction.TargetWorkerSessionId,
            TargetCharacterKey = instruction.TargetCharacterKey,
            StopGeneration = 9,
        };
        var result = Result(instruction);
        result.StopGeneration = 9;

        Assert.True(DadAlliancePartyFinderRules.TryValidateAsyncCancellationResult(
            8,
            8,
            cancellation,
            instruction,
            result,
            out var blocker), blocker);
        result.StopGeneration = 8;
        Assert.False(DadAlliancePartyFinderRules.TryValidateAsyncCancellationResult(
            8,
            8,
            cancellation,
            instruction,
            result,
            out _));
    }

    [Fact]
    public void ListingOwnershipSurvivesTerminalPresentationAndCleanupDeadlineIsFixed()
    {
        var blockedOwned = new DadAlliancePartyFinderStatus
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            State = DadAllianceRecruitmentState.Blocked,
            OwnsRecruitment = true,
        };
        var stoppedOwned = blockedOwned.Clone();
        stoppedOwned.State = DadAllianceRecruitmentState.Stopped;

        Assert.True(DadAllianceRemoteHostRules.HasActiveOperation(
            blockedOwned, false, false, false));
        Assert.True(DadAllianceRemoteHostRules.HasActiveOperation(
            stoppedOwned, false, false, false));

        var deadline = DadAllianceRemoteHostRules.GetFixedCleanupDeadline(null, Now);
        Assert.Equal(
            deadline,
            DadAllianceRemoteHostRules.GetFixedCleanupDeadline(deadline, Now.AddSeconds(30)));
        Assert.False(DadAllianceRemoteHostRules.CleanupExpired(deadline, deadline.AddTicks(-1)));
        Assert.True(DadAllianceRemoteHostRules.CleanupExpired(deadline, deadline));
    }

    [Fact]
    public void RemoteHostCleanupBlockedRemainsPendingAndAuditsBackOffBoundedly()
    {
        var blocked = new DadAllianceRecruitmentResultDto
        {
            ResultKind = DadAllianceRecruitmentResultKind.Blocked,
            State = DadAllianceRecruitmentState.Blocked,
        };

        Assert.Equal(
            DadAllianceRemoteHostLifecycleState.CleanupPending,
            DadAllianceRemoteHostRules.Evaluate(true, true, true, blocked));
        Assert.Equal(TimeSpan.FromMilliseconds(750), DadAllianceRemoteHostRules.GetAuditBackoff(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), DadAllianceRemoteHostRules.GetAuditBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(10), DadAllianceRemoteHostRules.GetAuditBackoff(99));
    }

    [Fact]
    public void TerminalPartialClearsOnlyAfterInactiveRecruitmentObservation()
    {
        Assert.False(DadAllianceRemoteHostRules.CanClearTerminalPartial(
            cleanupTerminalPartial: false,
            activeRecruitment: false));
        Assert.False(DadAllianceRemoteHostRules.CanClearTerminalPartial(
            cleanupTerminalPartial: true,
            activeRecruitment: true));
        Assert.True(DadAllianceRemoteHostRules.CanClearTerminalPartial(
            cleanupTerminalPartial: true,
            activeRecruitment: false));
    }

    [Fact]
    public void RemoteTerminalCleanupAuditRequiresExactStoppedSnapshot()
    {
        var instruction = Instruction();
        instruction.AssignedAlliance = DadAllianceAssignment.A;
        instruction.CreateListingAsHost = true;
        var snapshot = StoppedSnapshot(instruction, stopGeneration: 9);

        Assert.True(DadAllianceRemoteHostRules.TryValidateTerminalCleanupSnapshot(
            instruction,
            9,
            snapshot,
            out var blocker), blocker);

        var contradictions = new Action<DadAlliancePfUiSnapshotDto>[]
        {
            value => value.RecruitmentId = Guid.NewGuid().ToString("N"),
            value => value.WorkerSessionId = new DadWorkerSessionId("spoofed"),
            value => value.TargetCharacterKey = new DadCharacterKey("Other@Target World"),
            value => value.AssignedAlliance = DadAllianceAssignment.B,
            value => value.Attempt++,
            value => value.StopGeneration--,
            value => value.State = DadAllianceRecruitmentState.Blocked,
            value => value.SafeStatusCode = "dad-alliance-blocked",
        };
        foreach (var contradict in contradictions)
        {
            var invalid = StoppedSnapshot(instruction, stopGeneration: 9);
            contradict(invalid);
            Assert.False(DadAllianceRemoteHostRules.TryValidateTerminalCleanupSnapshot(
                instruction,
                9,
                invalid,
                out _));
        }
    }

    [Fact]
    public void AllianceHydrationNeedsTwoDistinctMatchingObservations()
    {
        var tracker = new DadStableAllianceHydrationTracker();

        Assert.Equal(DadAllianceAssignment.None, tracker.Observe(42, DadAllianceAssignment.C, 1));
        Assert.Equal(DadAllianceAssignment.None, tracker.Observe(42, DadAllianceAssignment.C, 1));
        Assert.Equal(DadAllianceAssignment.C, tracker.Observe(42, DadAllianceAssignment.C, 2));
        Assert.Equal(DadAllianceAssignment.None, tracker.Observe(42, DadAllianceAssignment.B, 3));
        Assert.Equal(DadAllianceAssignment.B, tracker.Observe(42, DadAllianceAssignment.B, 4));
        Assert.Equal(DadAllianceAssignment.None, tracker.Observe(99, DadAllianceAssignment.B, 5));
    }

    [Fact]
    public void RecoverableCleanupBlockRetriesWithoutDroppingOwnership()
    {
        var now = Now;
        var ui = new CleanupUi
        {
            Snapshot = new DadAlliancePfCleanupSnapshot
            {
                ActiveRecruitment = true,
                HardBlocker = "missing control",
            },
        };
        var flow = new DadAlliancePartyFinderCleanupFlow(ui, () => now);

        var blocked = flow.Advance(dadOwnsRecruitment: true);
        Assert.Equal(DadAlliancePfCreateResultKind.Blocked, blocked.Kind);

        ui.Snapshot = ui.Snapshot with { HardBlocker = string.Empty };
        now = blocked.NextRetryUtc!.Value;
        var retried = flow.Advance(dadOwnsRecruitment: true);

        Assert.Equal(DadAlliancePfCreateResultKind.Progress, retried.Kind);
        Assert.Equal(DadAlliancePfNativeAction.ShowOwnedRecruitment, Assert.Single(ui.Actions));
    }

    private static DadPromptApprovalRequest PromptRequest(DadPromptObservation current)
        => new(
            DadPromptOperationKind.PartyInvitationAcceptance,
            "operation",
            "operation",
            1,
            1,
            0,
            new DadPromptObservation(false, false, string.Empty, string.Empty, false),
            current,
            "Expected Leader",
            false);

    private static DadAllianceRecruitmentInstructionDto Instruction()
        => new()
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            CoordinatorWorkerSessionId = new DadWorkerSessionId("coordinator"),
            CoordinatorIdentity = "coordinator-identity",
            LeaderName = "Expected Leader",
            LeaderWorld = "Expected World",
            TargetWorkerSessionId = new DadWorkerSessionId("worker"),
            TargetCharacterKey = new DadCharacterKey("Target Character@Target World"),
            TargetCharacterName = "Target Character",
            TargetCharacterWorld = "Target World",
            TargetContentId = 42,
            AssignedAlliance = DadAllianceAssignment.C,
            Passcode = 1234,
            Attempt = 3,
            StopGeneration = 4,
            IssuedAtUtc = Now,
        };

    private static DadAllianceRecruitmentResultDto Result(
        DadAllianceRecruitmentInstructionDto instruction)
        => new()
        {
            RecruitmentId = instruction.RecruitmentId,
            WorkerSessionId = instruction.TargetWorkerSessionId,
            TargetCharacterKey = instruction.TargetCharacterKey,
            TargetCharacterName = instruction.TargetCharacterName,
            TargetCharacterWorld = instruction.TargetCharacterWorld,
            TargetContentId = instruction.TargetContentId,
            ExpectedAlliance = instruction.AssignedAlliance,
            ObservedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            StopGeneration = instruction.StopGeneration,
            ResultKind = DadAllianceRecruitmentResultKind.Succeeded,
            State = DadAllianceRecruitmentState.Complete,
        };

    private static DadAlliancePfUiSnapshotDto StoppedSnapshot(
        DadAllianceRecruitmentInstructionDto instruction,
        long stopGeneration)
        => new()
        {
            RecruitmentId = instruction.RecruitmentId,
            WorkerSessionId = instruction.TargetWorkerSessionId,
            TargetCharacterKey = instruction.TargetCharacterKey,
            AssignedAlliance = instruction.AssignedAlliance,
            ObservedAlliance = instruction.AssignedAlliance,
            Attempt = instruction.Attempt,
            State = DadAllianceRecruitmentState.Stopped,
            StopGeneration = stopGeneration,
            UpdatedAtUtc = Now,
            SafeStatusCode = DadAllianceRemoteHostRules.StoppedSafeStatusCode,
        };

    private static IEnumerable<DadAllianceRecruitmentResultDto> ContradictedResults(
        DadAllianceRecruitmentResultDto source)
    {
        var recruitment = source.Clone();
        recruitment.RecruitmentId = Guid.NewGuid().ToString("N");
        yield return recruitment;
        var worker = source.Clone();
        worker.WorkerSessionId = new DadWorkerSessionId("spoofed");
        yield return worker;
        var character = source.Clone();
        character.TargetCharacterKey = new DadCharacterKey("Other@Target World");
        yield return character;
        var content = source.Clone();
        content.TargetContentId++;
        yield return content;
        var alliance = source.Clone();
        alliance.ExpectedAlliance = DadAllianceAssignment.B;
        yield return alliance;
        var attempt = source.Clone();
        attempt.Attempt++;
        yield return attempt;
        var stop = source.Clone();
        stop.StopGeneration++;
        yield return stop;
    }

    private sealed class CleanupUi : IDadAlliancePartyFinderCleanupUi
    {
        public DadAlliancePfCleanupSnapshot Snapshot { get; set; } = new();
        public List<DadAlliancePfNativeAction> Actions { get; } = [];

        public DadAlliancePfCleanupSnapshot ReadCleanup() => Snapshot;

        public DadAlliancePfCreateActionResult PerformCleanup(DadAlliancePfNativeAction action)
        {
            Actions.Add(action);
            return new DadAlliancePfCreateActionResult(true, $"sent {action}");
        }
    }
}
