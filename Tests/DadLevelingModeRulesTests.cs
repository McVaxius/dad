using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLevelingModeRulesTests
{
    [Theory]
    [InlineData(DadRunStatus.Idle)]
    [InlineData(DadRunStatus.Queued)]
    [InlineData(DadRunStatus.WaitingForParticipants)]
    [InlineData(DadRunStatus.Running)]
    public void ActiveOrReconnectChildStatesKeepOuterOperationWaiting(DadRunStatus status)
        => Assert.Equal(
            DadLevelingChildDisposition.Waiting,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.StartedPlanner, status, dryRun: false));

    [Fact]
    public void SuccessfulChildRefreshesAndContinuesWhileFailureNeverReplays()
    {
        Assert.Equal(
            DadLevelingChildDisposition.RefreshAndContinue,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.StartedPlanner, DadRunStatus.Completed, dryRun: false));
        Assert.Equal(
            DadLevelingChildDisposition.Fail,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.StartedPlanner, DadRunStatus.Failed, dryRun: false));
        Assert.Equal(
            DadLevelingChildDisposition.Fail,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.TimedOut, null, dryRun: false));
    }

    [Fact]
    public void CancellationAndDryRunHaveTerminalOuterDispositions()
    {
        Assert.Equal(
            DadLevelingChildDisposition.Cancel,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.StartedPlanner, DadRunStatus.Cancelled, dryRun: false));
        Assert.Equal(
            DadLevelingChildDisposition.Cancel,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.Cancelled, null, dryRun: false));
        Assert.Equal(
            DadLevelingChildDisposition.CompleteDryRun,
            DadLevelingOperationRules.ClassifyChild(DadSchedulerPresetPhase.Completed, null, dryRun: true));
        Assert.True(new DadSchedulerPresetState { Phase = DadSchedulerPresetPhase.LevelingBetweenChildren }.IsActive);
    }

    [Fact]
    public void ExactRosterRefreshRejectsStaleContradictoryOrIncompleteReplies()
    {
        var command = new DadRosterRefreshCommandDto
        {
            CommandId = "refresh-1",
            AccountKey = new DadAccountKey("A"),
            CharacterKey = new DadCharacterKey("One@Alpha"),
            ContentId = 1001,
        };
        var result = new DadRosterRefreshResultDto
        {
            CommandId = command.CommandId,
            AccountKey = command.AccountKey,
            CharacterKey = command.CharacterKey,
            ContentId = command.ContentId,
            Accepted = true,
            Success = true,
            RefreshedAtUtc = DateTime.UtcNow,
            XadbStatus = new DadXadbStatus
            {
                IsReady = true,
                JobLevels = new Dictionary<uint, int> { [19] = 31 },
            },
        };

        Assert.True(DadLevelingOperationRules.TryValidateExactRosterRefresh(command, result, out var blocker), blocker);

        result.CommandId = "stale";
        Assert.False(DadLevelingOperationRules.TryValidateExactRosterRefresh(command, result, out blocker));
        Assert.Contains("identity", blocker, StringComparison.OrdinalIgnoreCase);

        result.CommandId = command.CommandId;
        result.XadbStatus.JobLevels.Clear();
        Assert.False(DadLevelingOperationRules.TryValidateExactRosterRefresh(command, result, out blocker));
        Assert.Contains("complete", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LowestFirstUsesLevelThenJobIdAsDeterministicTieBreak()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        var pool = Pool(Character("A", "One@Alpha", currentJob: 19, (19, 30), (21, 30), (32, 45)));

        var result = Compile(group, pool);

        Assert.Equal(DadLevelingCompilationStatus.Ready, result.Status);
        Assert.Equal((uint)19, Assert.Single(result.Slots).JobId);
        Assert.Equal(30, result.PartyMinimumLevel);
    }

    [Fact]
    public void HighestBelowGoalUsesHighestLevelThenJobId()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        group.LevelingMode.JobOrder = DadLevelingJobOrder.HighestBelowGoal;
        var pool = Pool(Character("A", "One@Alpha", currentJob: 19, (19, 30), (21, 70), (32, 70)));

        var result = Compile(group, pool);

        Assert.Equal((uint)21, Assert.Single(result.Slots).JobId);
    }

    [Fact]
    public void RoleAndAnySelectionExcludeBaseClassesAndLimitedJobs()
    {
        var group = Group(
            DadPlannerActivityMode.PremadeDuty,
            Slot("Slot1", DadPartyRole.Healer, "A", "One@Alpha"),
            Slot("Slot2", DadPartyRole.Any, "B", "Two@Alpha"));
        var pool = Pool(
            Character("A", "One@Alpha", currentJob: 24, (1, 1), (24, 40), (36, 10)),
            Character("B", "Two@Alpha", currentJob: 19, (1, 1), (19, 50), (36, 2)));

        var result = Compile(group, pool, premadeQueueSize: 2);

        Assert.Equal(DadLevelingCompilationStatus.Ready, result.Status);
        Assert.Equal((uint)24, result.Slots[0].JobId);
        Assert.Equal((uint)19, result.Slots[1].JobId);
        Assert.DoesNotContain(result.Slots, static slot => slot.JobId is 1 or 36);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unknown")]
    [InlineData("")]
    public void UnknownOrPartialJobLedgerBlocksSelection(string quality)
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        var character = Character("A", "One@Alpha", 19, (19, 30));
        character.SnapshotQuality = quality;

        var result = Compile(group, Pool(character));

        Assert.Equal(DadLevelingCompilationStatus.Blocked, result.Status);
        Assert.Contains("quality", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoEligibleUnlockedJobIsBlockerNotCompletion()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Healer, "A", "One@Alpha"));
        var pool = Pool(Character("A", "One@Alpha", currentJob: 19, (19, 100), (36, 100)));

        var result = Compile(group, pool);

        Assert.Equal(DadLevelingCompilationStatus.Blocked, result.Status);
        Assert.Contains("no unlocked full combat jobs", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletedSlotRetainsCompatibleCurrentJobAsFiller()
    {
        var group = Group(
            DadPlannerActivityMode.PremadeDuty,
            Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"),
            Slot("Slot2", DadPartyRole.Healer, "B", "Two@Alpha"));
        group.LevelingMode.GoalLevel = 50;
        var pool = Pool(
            Character("A", "One@Alpha", currentJob: 19, (19, 60), (21, 90)),
            Character("B", "Two@Alpha", currentJob: 24, (24, 20), (28, 30)));

        var result = Compile(group, pool, premadeQueueSize: 2);

        Assert.Equal(DadLevelingCompilationStatus.Ready, result.Status);
        Assert.True(result.Slots[0].IsFiller);
        Assert.Equal((uint)19, result.Slots[0].JobId);
        Assert.False(result.Slots[1].IsFiller);
    }

    [Fact]
    public void CompletedSlotFallsBackToHighestCompatibleJob()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        group.LevelingMode.GoalLevel = 50;
        var pool = Pool(Character("A", "One@Alpha", currentJob: 24, (19, 60), (21, 90)));

        var result = Compile(group, pool);

        Assert.Equal(DadLevelingCompilationStatus.Complete, result.Status);
        Assert.Equal((uint)21, Assert.Single(result.Slots).JobId);
        Assert.True(result.Slots[0].IsFiller);
    }

    [Fact]
    public void PlanCompletesOnlyWhenEveryActiveSlotIsComplete()
    {
        var group = Group(
            DadPlannerActivityMode.PremadeDuty,
            Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"),
            Slot("Slot2", DadPartyRole.Healer, "B", "Two@Alpha"));
        group.LevelingMode.GoalLevel = 50;
        var pool = Pool(
            Character("A", "One@Alpha", 19, (19, 50), (21, 70)),
            Character("B", "Two@Alpha", 24, (24, 50), (28, 60)));

        var result = Compile(group, pool, premadeQueueSize: 2);

        Assert.Equal(DadLevelingCompilationStatus.Complete, result.Status);
        Assert.Null(result.ChildGroup);
        Assert.All(result.Slots, static slot => Assert.True(slot.SlotComplete));
    }

    [Fact]
    public void HighestThresholdAtOrBelowPartyMinimumIsSelected()
    {
        var group = Group(
            DadPlannerActivityMode.PremadeDuty,
            Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"),
            Slot("Slot2", DadPartyRole.Healer, "B", "Two@Alpha"));
        group.LevelingMode.DutyThresholds =
        [
            new DadLevelingDutyThreshold { MinimumLevel = 1, ContentFinderConditionId = 100, DutyDisplayName = "Low" },
            new DadLevelingDutyThreshold { MinimumLevel = 30, ContentFinderConditionId = 200, DutyDisplayName = "Mid" },
            new DadLevelingDutyThreshold { MinimumLevel = 50, ContentFinderConditionId = 300, DutyDisplayName = "High" },
        ];
        var pool = Pool(
            Character("A", "One@Alpha", 19, (19, 45)),
            Character("B", "Two@Alpha", 24, (24, 30)));
        var duties = new[]
        {
            Duty(100, "Low", 1, 2), Duty(200, "Mid", 30, 2), Duty(300, "High", 50, 2),
        };

        var result = DadLevelingModeCompiler.Compile(group, pool, Jobs(), duties, idFactory: IdFactory());

        Assert.Equal(30, result.PartyMinimumLevel);
        Assert.Equal((uint)200, result.SelectedDuty?.ContentFinderConditionId);
    }

    [Fact]
    public void NoApplicableThresholdBlocksWithoutFallback()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        group.LevelingMode.DutyThresholds =
        [
            new DadLevelingDutyThreshold { MinimumLevel = 50, ContentFinderConditionId = 100, DutyDisplayName = "Later" },
        ];
        var pool = Pool(Character("A", "One@Alpha", 19, (19, 30)));

        var result = DadLevelingModeCompiler.Compile(group, pool, Jobs(), [Duty(100, "Later", 50, 4)], idFactory: IdFactory());

        Assert.Equal(DadLevelingCompilationStatus.Blocked, result.Status);
        Assert.Contains("no Leveling Mode duty threshold applies", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.SelectedDuty);
    }

    [Theory]
    [InlineData(30, 30, "strictly increasing")]
    [InlineData(40, 20, "strictly increasing")]
    public void DuplicateOrOutOfOrderThresholdsAreRejected(int first, int second, string expected)
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        group.LevelingMode.DutyThresholds =
        [
            new DadLevelingDutyThreshold { MinimumLevel = first, ContentFinderConditionId = 100 },
            new DadLevelingDutyThreshold { MinimumLevel = second, ContentFinderConditionId = 200 },
        ];

        var result = DadLevelingModeCompiler.Compile(
            group,
            Pool(Character("A", "One@Alpha", 19, (19, 50))),
            Jobs(),
            [Duty(100, "One", 1, 4), Duty(200, "Two", 1, 4)],
            idFactory: IdFactory());

        Assert.Equal(DadLevelingCompilationStatus.Blocked, result.Status);
        Assert.Contains(expected, result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThresholdRejectsUnavailableRequiredLevelAndLaneMismatch()
    {
        var group = Group(DadPlannerActivityMode.Trust, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        group.LevelingMode.DutyThresholds =
        [
            new DadLevelingDutyThreshold { MinimumLevel = 10, ContentFinderConditionId = 100 },
            new DadLevelingDutyThreshold { MinimumLevel = 20, ContentFinderConditionId = 999 },
        ];
        var duty = Duty(100, "Support only", required: 15, queueSize: 4, support: true, trust: false);

        var result = DadLevelingModeCompiler.Compile(
            group,
            Pool(Character("A", "One@Alpha", 19, (19, 50))),
            Jobs(),
            [duty],
            idFactory: IdFactory());

        Assert.Contains("below", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incompatible", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable duty 999", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PremadeDutyRejectsQueueSizeThatDoesNotMatchFixedCrew()
    {
        var group = Group(
            DadPlannerActivityMode.PremadeDuty,
            Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"),
            Slot("Slot2", DadPartyRole.Healer, "B", "Two@Alpha"));
        var pool = Pool(
            Character("A", "One@Alpha", 19, (19, 20)),
            Character("B", "Two@Alpha", 24, (24, 20)));

        var result = Compile(group, pool, premadeQueueSize: 4);

        Assert.Equal(DadLevelingCompilationStatus.Blocked, result.Status);
        Assert.Contains("fixed Leveling Mode crew has 2", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DadPlannerActivityMode.DutySupport, DadPlannerActivityMode.DutySupport)]
    [InlineData(DadPlannerActivityMode.DutySupportLeveling, DadPlannerActivityMode.DutySupport)]
    [InlineData(DadPlannerActivityMode.Trust, DadPlannerActivityMode.Trust)]
    [InlineData(DadPlannerActivityMode.TrustLeveling, DadPlannerActivityMode.Trust)]
    [InlineData(DadPlannerActivityMode.DutyPremade, DadPlannerActivityMode.PremadeDuty)]
    [InlineData(DadPlannerActivityMode.PremadeDuty, DadPlannerActivityMode.PremadeDuty)]
    public void SupportedLanesCompileSyncedOrdinaryChild(
        DadPlannerActivityMode sourceLane,
        DadPlannerActivityMode expectedChildLane)
    {
        var npc = expectedChildLane is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust;
        var slots = npc
            ? new[] { Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha") }
            : new[]
            {
                Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"),
                Slot("Slot2", DadPartyRole.Healer, "B", "Two@Alpha"),
            };
        var group = Group(sourceLane, slots);
        var pool = npc
            ? Pool(Character("A", "One@Alpha", 19, (19, 20)))
            : Pool(Character("A", "One@Alpha", 19, (19, 20)), Character("B", "Two@Alpha", 24, (24, 20)));

        var result = Compile(group, pool, premadeQueueSize: slots.Length);

        Assert.Equal(DadLevelingCompilationStatus.Ready, result.Status);
        Assert.Equal(expectedChildLane, result.ChildGroup?.ActivityMode);
        Assert.False(result.ChildGroup?.DutyUnsynced);
        Assert.False(result.ChildGroup?.LevelingMode.Enabled);
    }

    [Fact]
    public void FrozenChildOwnsUniqueIdsAndOverridesConflictsWithoutMutatingSavedPlan()
    {
        var sourceSlot = Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha");
        sourceSlot.RequiredJobId = 32;
        sourceSlot.LevelSeekTarget = 99;
        sourceSlot.SkipIfDailyRouletteRewardReceived = true;
        var group = Group(DadPlannerActivityMode.DutySupport, sourceSlot);
        group.DutyContentFinderConditionId = 999;
        group.DutyDisplayName = "Saved duty";
        group.DutyUnsynced = true;
        group.StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.TargetLevel, TargetLevel = 88, SafetyCap = 12 };
        var ids = new Queue<string>(["child-job", "child-request"]);

        var result = DadLevelingModeCompiler.Compile(
            group,
            Pool(Character("A", "One@Alpha", 19, (19, 20), (21, 30), (32, 40))),
            Jobs(),
            [Duty(100, "First", 1, 4)],
            idFactory: () => ids.Dequeue());

        Assert.True(result.CanStartChild);
        Assert.Equal("child-job", result.ChildJobId);
        Assert.Equal("child-request", result.ChildRequestId);
        Assert.NotEqual(result.ChildJobId, result.ChildRequestId);
        var child = Assert.IsType<DadPlannerGroup>(result.ChildGroup);
        var childSlot = Assert.Single(child.Slots);
        Assert.Equal((uint)19, childSlot.RequiredJobId);
        Assert.Null(childSlot.LevelSeekTarget);
        Assert.False(childSlot.SkipIfDailyRouletteRewardReceived);
        Assert.Equal(DadPlannerStopMode.AfterRuns, child.StopPolicy.Mode);
        Assert.Equal(1, child.StopPolicy.AfterRuns);
        Assert.Equal((uint)100, child.DutyContentFinderConditionId);
        Assert.False(child.DutyUnsynced);

        Assert.Equal((uint)32, group.Slots[0].RequiredJobId);
        Assert.Equal(99, group.Slots[0].LevelSeekTarget);
        Assert.True(group.Slots[0].SkipIfDailyRouletteRewardReceived);
        Assert.True(group.DutyUnsynced);
        Assert.Equal(DadPlannerStopMode.TargetLevel, group.StopPolicy.Mode);
    }

    [Fact]
    public void ConsecutiveCompilationsNeverReuseChildJobOrRequestIds()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        var pool = Pool(Character("A", "One@Alpha", 19, (19, 20)));
        var ids = new Queue<string>(["job-1", "request-1", "job-2", "request-2"]);
        var first = DadLevelingModeCompiler.Compile(group, pool, Jobs(), [Duty(100, "First", 1, 4)], 1, () => ids.Dequeue());
        var second = DadLevelingModeCompiler.Compile(group, pool, Jobs(), [Duty(100, "First", 1, 4)], 2, () => ids.Dequeue());

        Assert.Equal(4, new[] { first.ChildJobId, first.ChildRequestId, second.ChildJobId, second.ChildRequestId }.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, first.Iteration);
        Assert.Equal(2, second.Iteration);
    }

    [Fact]
    public void NpcLaneUsesOnlyFixedLeaderSlot()
    {
        var group = Group(
            DadPlannerActivityMode.DutySupport,
            Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"),
            Slot("Slot2", DadPartyRole.Healer, "B", "Two@Alpha"));

        var result = Compile(group, Pool(Character("A", "One@Alpha", 19, (19, 20))));

        Assert.Equal(DadLevelingCompilationStatus.Ready, result.Status);
        Assert.Single(result.Slots);
        Assert.Single(Assert.IsType<DadPlannerGroup>(result.ChildGroup).Slots);
    }

    [Fact]
    public void ExactFixedIdentityAndCompleteLedgerAreMandatory()
    {
        var group = Group(DadPlannerActivityMode.DutySupport, Slot("Slot1", DadPartyRole.Tank, "A", "One@Alpha"));
        var wrongAccount = Character("B", "One@Alpha", 19, (19, 20));

        var result = Compile(group, Pool(wrongAccount));

        Assert.Equal(DadLevelingCompilationStatus.Blocked, result.Status);
        Assert.Contains("could not resolve exact character", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static DadLevelingCompilation Compile(
        DadPlannerGroup group,
        DadCharacterPool pool,
        int premadeQueueSize = 1)
    {
        var lane = group.ActivityMode is DadPlannerActivityMode.Trust or DadPlannerActivityMode.TrustLeveling;
        return DadLevelingModeCompiler.Compile(
            group,
            pool,
            Jobs(),
            [Duty(100, "First", 1, premadeQueueSize, support: !lane, trust: lane)],
            idFactory: IdFactory());
    }

    private static DadPlannerGroup Group(DadPlannerActivityMode lane, params DadPlannerGroupSlot[] slots)
        => new()
        {
            GroupId = "leveling-plan",
            DisplayName = "Leveling plan",
            ActivityMode = lane,
            RunFamily = lane is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust
                or DadPlannerActivityMode.DutySupportLeveling or DadPlannerActivityMode.TrustLeveling
                ? DadPlannerRunFamily.LevelingNpc
                : DadPlannerRunFamily.DutyFinder,
            Slots = [.. slots],
            LevelingMode = new DadLevelingModeOptions
            {
                Enabled = true,
                GoalLevel = 100,
                JobOrder = DadLevelingJobOrder.LowestFirst,
                DutyThresholds =
                [
                    new DadLevelingDutyThreshold
                    {
                        MinimumLevel = 1,
                        ContentFinderConditionId = 100,
                        DutyDisplayName = "First",
                    },
                ],
            },
        };

    private static DadPlannerGroupSlot Slot(
        string slotId,
        DadPartyRole role,
        string account,
        string character)
        => new()
        {
            SlotId = slotId,
            RequiredRole = role,
            RequiredAccountKey = new DadAccountKey(account),
            RequiredCharacterKey = new DadCharacterKey(character),
        };

    private static DadCharacterPool Pool(params DadAcquiredCharacter[] characters)
        => new() { Characters = [.. characters] };

    private static DadAcquiredCharacter Character(
        string account,
        string character,
        uint currentJob,
        params (uint JobId, int Level)[] levels)
        => new()
        {
            AccountId = account,
            CharacterKey = character,
            ContentId = (ulong)(1000 + account[0]),
            CurrentJobId = currentJob,
            JobLevels = levels.ToDictionary(static level => level.JobId, static level => level.Level),
            XadbReady = true,
            XadbSnapshotUtc = DateTime.UtcNow,
            SnapshotVersion = 1,
            SnapshotQuality = "full",
        };

    private static IReadOnlyList<DadLevelingJobDescriptor> Jobs()
        =>
        [
            Job(1, "GLA", DadPartyRole.Tank, full: false),
            Job(19, "PLD", DadPartyRole.Tank),
            Job(21, "WAR", DadPartyRole.Tank),
            Job(32, "DRK", DadPartyRole.Tank),
            Job(24, "WHM", DadPartyRole.Healer),
            Job(28, "SCH", DadPartyRole.Healer),
            Job(20, "MNK", DadPartyRole.Melee),
            Job(23, "BRD", DadPartyRole.PhysicalRanged),
            Job(25, "BLM", DadPartyRole.Caster),
            Job(36, "BLU", DadPartyRole.Caster, limited: true),
        ];

    private static DadLevelingJobDescriptor Job(
        uint id,
        string abbreviation,
        DadPartyRole role,
        bool full = true,
        bool limited = false)
        => new()
        {
            JobId = id,
            Abbreviation = abbreviation,
            Role = role,
            IsFullCombatJob = full,
            IsLimitedJob = limited,
        };

    private static DadPlannerDutyOption Duty(
        uint id,
        string name,
        int required,
        int queueSize,
        bool support = true,
        bool trust = true)
        => new()
        {
            ContentFinderConditionId = id,
            DutyDisplayName = name,
            JobLevelRequired = required,
            QueueSize = queueSize,
            SupportsDutySupport = support,
            SupportsTrust = trust,
        };

    private static Func<string> IdFactory()
    {
        var next = 0;
        return () => $"id-{++next}";
    }
}
