using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPostReadinessLevelSeekRulesTests
{
    [Fact]
    public void SpecificJobRowsSkipAfterTheAcknowledgedJobLedgerReachesTarget()
    {
        var group = Group(Slot("one", 70, jobId: 21));
        var character = Character("one", currentJobId: 19, currentLevel: 30, (21, 70));

        var evaluation = DadLevelSeekEvaluationRules.Evaluate(group, new DadRunStopPolicy(), Pool(character));

        Assert.True(evaluation.ShouldSkip);
        Assert.Contains("job 21 is level 70", evaluation.DescribeEvidence());
    }

    [Fact]
    public void AnyJobRowsSkipAfterTheLoadedCurrentJobReachesTarget()
    {
        var group = Group(Slot("one", 70));
        var character = Character("one", currentJobId: 19, currentLevel: 70);
        MakeLive(character);

        var evaluation = DadLevelSeekEvaluationRules.Evaluate(group, new DadRunStopPolicy(), Pool(character));

        Assert.True(evaluation.ShouldSkip);
        Assert.Contains("job 19 is level 70", evaluation.DescribeEvidence());
    }

    [Fact]
    public void ExplicitJobSkipsFromLedgerWithoutLiveCurrentJob()
    {
        var group = Group(Slot("one", 70, jobId: 21));
        var character = Character("one", currentJobId: null, currentLevel: null, (21, 70));

        var evaluation = DadLevelSeekEvaluationRules.Evaluate(group, new DadRunStopPolicy(), Pool(character));
        var display = DadLevelSeekDisplayRules.Build(evaluation);

        Assert.True(evaluation.ShouldSkip);
        Assert.Equal(DadLevelSeekRowState.Satisfied, Assert.Single(evaluation.Rows).State);
        Assert.True(display.IsSkipIndicated);
        Assert.Contains("exact job evidence", display.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnyJobIgnoresStaleLedgerUntilLiveJobAndLevelExistThenReevaluates()
    {
        var group = Group(Slot("one", 70));
        var character = Character("one", currentJobId: 19, currentLevel: 70, (19, 99));

        var stale = DadLevelSeekEvaluationRules.Evaluate(group, new DadRunStopPolicy(), Pool(character));
        var staleDisplay = DadLevelSeekDisplayRules.Build(stale);

        Assert.False(stale.ShouldSkip);
        Assert.Equal(DadLevelSeekRowState.Unknown, Assert.Single(stale.Rows).State);
        Assert.False(staleDisplay.IsSkipIndicated);
        Assert.Contains("live current job and level", staleDisplay.Tooltip, StringComparison.OrdinalIgnoreCase);

        MakeLive(character);
        var live = DadLevelSeekEvaluationRules.Evaluate(group, new DadRunStopPolicy(), Pool(character));
        var liveDisplay = DadLevelSeekDisplayRules.Build(live);

        Assert.True(live.ShouldSkip);
        Assert.Equal(DadLevelSeekRowState.Satisfied, Assert.Single(live.Rows).State);
        Assert.True(liveDisplay.IsSkipIndicated);
        Assert.Contains("live job 19 is level 70", liveDisplay.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvedTargetLevelPolicyRemainsTheAuthoritativeEvaluation()
    {
        var policy = new DadRunStopPolicy
        {
            Mode = DadPlannerStopMode.TargetLevel,
            ResolvedLevelTargets =
            [
                new DadResolvedLevelTarget
                {
                    CharacterKey = new DadCharacterKey("one"),
                    CharacterLabel = "one",
                    JobId = 21,
                    TargetLevel = 70,
                },
            ],
        };
        var group = Group(Slot("one", 99, jobId: 19));
        var character = Character("one", currentJobId: 19, currentLevel: 30, (21, 70));

        var evaluation = DadLevelSeekEvaluationRules.Evaluate(group, policy, Pool(character));

        Assert.True(evaluation.ShouldSkip);
        Assert.Contains("resolved level target", evaluation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlannerPreflightSkipsSatisfiedOfflineTargetDespiteDifferentLiveCharacter()
    {
        var group = Group(Slot("Target Character@World", 20, jobId: 26));
        var target = Character("Target Character@World", null, null, (26, 50));
        var liveOther = Character("Other Character@World", 26, 1, (26, 1));
        MakeLive(liveOther);
        var policy = new DadRunStopPolicy
        {
            Mode = DadPlannerStopMode.TargetLevel,
            ResolvedLevelTargets =
            [
                new DadResolvedLevelTarget
                {
                    CharacterKey = new DadCharacterKey(target.CharacterKey),
                    JobId = 26,
                    TargetLevel = 20,
                },
            ],
        };
        var preview = new DadPlannerRunRequestPreview
        {
            Request = new DadRunRequest { StopPolicy = policy },
            StopPolicy = policy,
            CanStart = false,
            CanSchedule = false,
            ReadinessBlockers = ["The account is logged in as another character."],
            PlannerPreview = new DadActivityPreset { AvailableCharacters = [target, liveOther] },
        };

        // This is the old scheduler's runtime pool: it cannot see the offline target.
        Assert.False(DadLevelSeekEvaluationRules.Evaluate(group, policy, Pool(liveOther)).ShouldSkip);
        var evaluation = DadLevelSeekEvaluationRules.EvaluatePlannerPreflight(group, preview);

        Assert.True(evaluation.ShouldSkip);
        Assert.Contains("ledger level 50 meets target 20", evaluation.DescribeEvidence());
    }

    [Fact]
    public void PlannerPreflightRequiresAllFourAccountTargetsToBeProvenSatisfied()
    {
        var slots = Enumerable.Range(0, 4).Select(index =>
        {
            var slot = Slot($"Character {index}@World", 60, jobId: 26);
            slot.SlotId = $"Slot{index + 1}";
            slot.RequiredAccountKey = new DadAccountKey($"account-{index}");
            return slot;
        }).ToArray();
        var characters = slots.Select((slot, index) =>
        {
            var character = Character(slot.RequiredCharacterKey.Value, null, null, (26, 60));
            character.AccountId = slot.RequiredAccountKey.Value;
            return character;
        }).ToList();
        var group = Group(slots);
        var preview = new DadPlannerRunRequestPreview
        {
            PlannerPreview = new DadActivityPreset { AvailableCharacters = characters },
        };

        Assert.True(DadLevelSeekEvaluationRules.EvaluatePlannerPreflight(group, preview).ShouldSkip);
        characters[3].JobLevels[26] = 59;
        Assert.False(DadLevelSeekEvaluationRules.EvaluatePlannerPreflight(group, preview).ShouldSkip);
        characters[3].JobLevels.Clear();
        Assert.False(DadLevelSeekEvaluationRules.EvaluatePlannerPreflight(group, preview).ShouldSkip);
    }

    [Fact]
    public void SatisfiedPreflightAdvancesScheduleCursorWithoutReadiness()
    {
        var group = Group(Slot("offline", 20, jobId: 26));
        var preview = new DadPlannerRunRequestPreview
        {
            PlannerPreview = new DadActivityPreset
            {
                AvailableCharacters = [Character("offline", null, null, (26, 50))],
            },
        };
        var evaluation = DadLevelSeekEvaluationRules.EvaluatePlannerPreflight(group, preview);
        Assert.True(evaluation.ShouldSkip);
        var entries = new List<DadScheduleEntry>
        {
            new() { EntryId = "sastasha", GroupId = "sastasha", PresetName = "Sastasha", RepeatCount = 1 },
            new() { EntryId = "halatali", GroupId = "halatali", PresetName = "Halatali", RepeatCount = 1 },
        };
        var state = new DadScheduleRunState
        {
            ScheduleName = "Leveling",
            TotalEntryExecutions = 2,
            CurrentEntryIndex = 0,
            RepeatIteration = 1,
        };

        var next = DadScheduleRules.AdvanceAfterEntry(state, entries, true, evaluation.Summary, DateTime.UtcNow, entrySkipped: true);

        Assert.Equal(1, next.CurrentEntryIndex);
        Assert.Equal(1, next.SkippedEntryExecutions);
        Assert.Equal(DadScheduleRunStatus.Running, next.Status);
        Assert.Equal(DadScheduleRunPhase.StartingEntry, next.Phase);
        Assert.Empty(next.BlockedReason);
    }

    private static DadPlannerGroup Group(params DadPlannerGroupSlot[] slots)
        => new() { Slots = slots.ToList() };

    private static DadPlannerGroupSlot Slot(string characterKey, int target, uint? jobId = null)
        => new()
        {
            SlotId = "Slot1",
            RequiredAccountKey = new DadAccountKey("account"),
            RequiredCharacterKey = new DadCharacterKey(characterKey),
            RequiredJobId = jobId,
            LevelSeekTarget = target,
        };

    private static DadAcquiredCharacter Character(
        string characterKey,
        uint? currentJobId,
        int? currentLevel,
        params (uint JobId, int Level)[] jobLevels)
        => new()
        {
            CharacterKey = characterKey,
            AccountId = "account",
            CurrentJobId = currentJobId,
            CurrentLevel = currentLevel,
            JobLevels = jobLevels.ToDictionary(static pair => pair.JobId, static pair => pair.Level),
        };

    private static DadCharacterPool Pool(params DadAcquiredCharacter[] characters)
        => new() { Characters = characters.ToList() };

    private static void MakeLive(DadAcquiredCharacter character)
    {
        character.Source = DadCharacterSource.LocalRuntime;
        character.Freshness = DadSnapshotFreshness.Live;
        character.Readiness = DadReadinessState.Ready;
    }
}
