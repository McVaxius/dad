using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadResolvedLevelTargetRulesTests
{
    [Fact]
    public void SlotOneRowOverridesBottomAndAdditionalRowsAreAdditive()
    {
        var policy = DadResolvedLevelTargetRules.ResolvePolicy(
            new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
                TargetLevel = 90,
            },
            [
                Slot("Slot1", "One@Alpha", rowTarget: 80, jobId: 19),
                Slot("Slot2", "Two@Alpha", rowTarget: 70),
                Slot("Slot3", "Three@Alpha"),
            ],
            [
                Character("One@Alpha", 19, 50),
                Character("Two@Alpha", 21, 60),
                Character("Three@Alpha", 24, 100),
            ]);

        Assert.Collection(
            policy.ResolvedLevelTargets,
            target =>
            {
                Assert.Equal("One@Alpha", target.CharacterKey.Value);
                Assert.Equal((uint?)19, target.JobId);
                Assert.Equal(80, target.TargetLevel);
            },
            target =>
            {
                Assert.Equal("Two@Alpha", target.CharacterKey.Value);
                Assert.Null(target.JobId);
                Assert.Equal(70, target.TargetLevel);
            });
    }

    [Fact]
    public void BlankSlotOneInheritsBottomWhenAnotherRowEnablesAggregateTargets()
    {
        var policy = DadResolvedLevelTargetRules.ResolvePolicy(
            new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
                TargetLevel = 95,
            },
            [
                Slot("Slot1", "One@Alpha"),
                Slot("Slot2", "Two@Alpha", rowTarget: 75),
            ],
            [Character("One@Alpha", 19, 90), Character("Two@Alpha", 21, 70)]);

        Assert.Equal([95, 75], policy.ResolvedLevelTargets.Select(static target => target.TargetLevel));
    }

    [Fact]
    public void FirstSelectedPrimaryInheritsBottomEvenWhenEarlierRowsAreEmpty()
    {
        var policy = DadResolvedLevelTargetRules.ResolvePolicy(
            new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
                TargetLevel = 95,
            },
            [
                Slot("Slot1", string.Empty),
                Slot("Slot2", "Two@Alpha"),
                Slot("Slot3", "Three@Alpha", rowTarget: 75),
            ],
            [Character("Two@Alpha", 21, 90), Character("Three@Alpha", 24, 70)]);

        Assert.Equal(
            ["Two@Alpha", "Three@Alpha"],
            policy.ResolvedLevelTargets.Select(static target => target.CharacterKey.Value));
        Assert.Equal([95, 75], policy.ResolvedLevelTargets.Select(static target => target.TargetLevel));
    }

    [Fact]
    public void NoRowOverridePreservesScalarCompatibilityPath()
    {
        var policy = DadResolvedLevelTargetRules.ResolvePolicy(
            new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
                TargetLevel = 88,
            },
            [Slot("Slot1", "One@Alpha"), Slot("Slot2", "Two@Alpha")],
            [Character("One@Alpha", 19, 80), Character("Two@Alpha", 21, 80)]);

        Assert.Empty(policy.ResolvedLevelTargets);
        Assert.Equal("One@Alpha", policy.TargetCharacterKey.Value);
        Assert.Equal(88, policy.TargetLevel);
    }

    [Fact]
    public void SpecificJobUsesLedgerWhileAnyUsesLiveCurrentJob()
    {
        var policy = Policy(
            Target("One@Alpha", 90, jobId: 19),
            Target("Two@Alpha", 80));
        var one = Character("One@Alpha", 21, 100, (19, 91), (21, 100));
        var two = Character("Two@Alpha", 24, 81, (19, 100));

        var evaluation = DadResolvedLevelTargetRules.Evaluate(policy, Pool(one, two));

        Assert.True(evaluation.AllSatisfied);
        Assert.Collection(
            evaluation.Evidence,
            evidence =>
            {
                Assert.Equal((uint?)19, evidence.ObservedJobId);
                Assert.Equal(91, evidence.ObservedLevel);
            },
            evidence =>
            {
                Assert.Equal((uint?)24, evidence.ObservedJobId);
                Assert.Equal(81, evidence.ObservedLevel);
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void ZeroOneAndNullObservationsRemainUnknown(int? observedLevel)
    {
        var character = Character("One@Alpha", 19, observedLevel);
        character.JobLevels[19] = observedLevel ?? 0;
        var specific = DadResolvedLevelTargetRules.Evaluate(
            Policy(Target("One@Alpha", 1, jobId: 19)),
            Pool(character));
        var any = DadResolvedLevelTargetRules.Evaluate(
            Policy(Target("One@Alpha", 1)),
            Pool(character));

        Assert.False(specific.AllSatisfied);
        Assert.False(any.AllSatisfied);
        Assert.Equal(DadResolvedLevelTargetState.Unknown, Assert.Single(specific.Evidence).State);
        Assert.Equal(DadResolvedLevelTargetState.Unknown, Assert.Single(any.Evidence).State);
    }

    [Fact]
    public void FreshPostWakeAndPostRunEvidenceCanChangeAggregateDecision()
    {
        var policy = Policy(
            Target("One@Alpha", 90),
            Target("Two@Alpha", 90, jobId: 21));
        var beforeWake = Character("One@Alpha", 19, 90);
        beforeWake.Source = DadCharacterSource.XadbOnly;
        beforeWake.Freshness = DadSnapshotFreshness.Stale;
        beforeWake.Readiness = DadReadinessState.Unavailable;
        var below = Character("Two@Alpha", 21, 89, (21, 89));

        var initial = DadResolvedLevelTargetRules.Evaluate(policy, Pool(beforeWake, below));
        var postWake = DadResolvedLevelTargetRules.Evaluate(
            policy,
            Pool(Character("One@Alpha", 19, 90), Character("Two@Alpha", 21, 90, (21, 90))));
        var postRun = DadResolvedLevelTargetRules.Evaluate(
            policy,
            Pool(Character("One@Alpha", 19, 91), Character("Two@Alpha", 21, 92, (21, 92))));

        Assert.False(initial.AllSatisfied);
        Assert.Contains(initial.Evidence, static evidence =>
            evidence.State == DadResolvedLevelTargetState.Unknown);
        Assert.True(postWake.AllSatisfied);
        Assert.True(postRun.AllSatisfied);
        Assert.True(postWake.ToLevelSeekEvaluation().ShouldSkip);
    }

    [Fact]
    public void StopPolicyCloneAndRequestJsonDeepCopyResolvedTargets()
    {
        var request = new DadRunRequest
        {
            StopPolicy = Policy(Target("One@Alpha", 90, jobId: 19)),
        };
        var clone = request.StopPolicy.Clone();
        clone.ResolvedLevelTargets[0].TargetLevel = 99;
        var roundTrip = DadIpcJson.Deserialize<DadRunRequest>(DadIpcJson.Serialize(request))!;

        Assert.Equal(90, request.StopPolicy.ResolvedLevelTargets[0].TargetLevel);
        Assert.Equal(90, roundTrip.StopPolicy.ResolvedLevelTargets[0].TargetLevel);
        Assert.Equal((uint?)19, roundTrip.StopPolicy.ResolvedLevelTargets[0].JobId);
        Assert.Equal("One@Alpha", roundTrip.StopPolicy.ResolvedLevelTargets[0].CharacterKey.Value);
    }

    private static DadRunStopPolicy Policy(params DadResolvedLevelTarget[] targets)
        => new()
        {
            Mode = DadPlannerStopMode.TargetLevel,
            TargetLevel = 100,
            SafetyCap = 20,
            ResolvedLevelTargets = targets.ToList(),
        };

    private static DadResolvedLevelTarget Target(string key, int targetLevel, uint? jobId = null)
        => new()
        {
            CharacterKey = new DadCharacterKey(key),
            CharacterLabel = key,
            JobId = jobId,
            TargetLevel = targetLevel,
        };

    private static DadPresetCharacterSlot Slot(
        string slotId,
        string characterKey,
        int? rowTarget = null,
        uint? jobId = null)
        => new()
        {
            SlotId = slotId,
            CharacterKey = characterKey,
            RequiredCharacterKey = new DadCharacterKey(characterKey),
            RequiredJobId = jobId,
            LevelSeekTarget = rowTarget,
        };

    private static DadAcquiredCharacter Character(
        string key,
        uint? currentJobId,
        int? currentLevel,
        params (uint JobId, int Level)[] jobs)
        => new()
        {
            CharacterKey = key,
            CharacterName = key.Split('@')[0],
            WorldName = key.Contains('@') ? key.Split('@')[1] : "Alpha",
            ContentId = (ulong)Math.Abs(key.GetHashCode()) + 1,
            Source = DadCharacterSource.PeerRuntime,
            Freshness = DadSnapshotFreshness.Live,
            Readiness = DadReadinessState.Ready,
            CurrentJobId = currentJobId,
            CurrentLevel = currentLevel,
            JobLevels = jobs.ToDictionary(static job => job.JobId, static job => job.Level),
        };

    private static DadCharacterPool Pool(params DadAcquiredCharacter[] characters)
        => new() { Characters = characters.ToList() };
}
