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
