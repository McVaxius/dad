using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLevelingModeActivationRulesTests
{
    public static TheoryData<DadPlannerRunFamily, DadPlannerActivityMode, DadPlannerActivityMode> SupportedDrafts => new()
    {
        { DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.DutySupport, DadPlannerActivityMode.DutySupport },
        { DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.Trust, DadPlannerActivityMode.Trust },
        { DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.DutySupportLeveling, DadPlannerActivityMode.DutySupport },
        { DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.TrustLeveling, DadPlannerActivityMode.Trust },
        { DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.PremadeDuty, DadPlannerActivityMode.PremadeDuty },
        { DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.DutyPremade, DadPlannerActivityMode.PremadeDuty },
    };

    [Theory]
    [MemberData(nameof(SupportedDrafts))]
    public void SupportedDraftsIncludeCurrentAndLegacyNormalizedLanes(
        DadPlannerRunFamily runFamily,
        DadPlannerActivityMode activityMode,
        DadPlannerActivityMode expectedNormalizedActivity)
    {
        Assert.True(DadLevelingModeActivationRules.TryNormalizeSupportedDraft(
            runFamily,
            activityMode,
            out var normalizedFamily,
            out var normalizedActivity));
        Assert.Equal(runFamily, normalizedFamily);
        Assert.Equal(expectedNormalizedActivity, normalizedActivity);
    }

    [Fact]
    public void EveryOtherFamilyAndActivityCombinationIsUnsupported()
    {
        var supported = new HashSet<(DadPlannerRunFamily Family, DadPlannerActivityMode Activity)>
        {
            (DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.DutySupport),
            (DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.Trust),
            (DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.DutySupportLeveling),
            (DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.TrustLeveling),
            (DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.PremadeDuty),
            (DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.DutyPremade),
        };

        foreach (var family in Enum.GetValues<DadPlannerRunFamily>())
        foreach (var activity in Enum.GetValues<DadPlannerActivityMode>().Distinct())
        {
            var actual = DadLevelingModeActivationRules.TryNormalizeSupportedDraft(
                family,
                activity,
                out _,
                out _);
            Assert.Equal(supported.Contains((family, activity)), actual);
        }
    }

    [Fact]
    public void EnablingUsesVisibleDraftImmediatelyEvenWhenSavedLaneIsOld()
    {
        var target = Target(DadPlannerRunFamily.FarmLoops, DadPlannerActivityMode.CustomDuty);
        var draft = Draft(DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.DutySupport);
        var now = UtcNow();

        var result = DadLevelingModeActivationRules.Apply(target, draft, enabled: true, now);

        Assert.True(result.Accepted);
        Assert.True(result.Enabled);
        Assert.True(target.LevelingMode.Enabled);
        Assert.Equal(DadPlannerRunFamily.LevelingNpc, target.RunFamily);
        Assert.Equal(DadPlannerActivityMode.DutySupport, target.ActivityMode);
        Assert.Equal(now, target.UpdatedAtUtc);
    }

    [Fact]
    public void EnablingAtomicallyCopiesAllEstablishedPlannerFields()
    {
        var target = Target(DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.Trust);
        var draft = Draft(DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.PremadeDuty);
        draft.DisplayName = "Draft name";
        draft.OperatorMode = DadPlannerOperatorMode.TestOnThisMachine;
        draft.ConnectedOnly = false;
        draft.SameDatacenterOnly = false;
        draft.AllowStaleForPlanning = true;
        draft.TransportOwner = DadTransportOwner.LanParty;
        draft.QueueAuthority = DadQueueAuthority.Leader;
        draft.DutyContentFinderConditionId = 777;
        draft.DutyDisplayName = "Draft duty";
        draft.DutyUnsynced = true;
        draft.DutyExpectedPartySize = 8;
        draft.MogtomePreset = "draft preset";
        draft.MogtomeDutyPolicy = "draft policy";
        draft.RefreshTrustNpcLevels = false;
        draft.StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 7 };
        draft.CompletionActions = new DadCompletionActions { PlaySound = true, SoundEffectId = 9 };
        var now = UtcNow();

        var result = DadLevelingModeActivationRules.Apply(target, draft, enabled: true, now);

        Assert.True(result.Accepted);
        Assert.Equal(draft.DisplayName, target.DisplayName);
        Assert.Equal(draft.RunFamily, target.RunFamily);
        Assert.Equal(draft.ActivityMode, target.ActivityMode);
        Assert.Equal(draft.OperatorMode, target.OperatorMode);
        Assert.Equal(draft.ConnectedOnly, target.ConnectedOnly);
        Assert.Equal(draft.SameDatacenterOnly, target.SameDatacenterOnly);
        Assert.Equal(draft.AllowStaleForPlanning, target.AllowStaleForPlanning);
        Assert.Equal(draft.TransportOwner, target.TransportOwner);
        Assert.Equal(draft.QueueAuthority, target.QueueAuthority);
        Assert.Equal(DadInviteAuthority.PresetLeader, target.InviteAuthority);
        Assert.Equal(draft.DutyContentFinderConditionId, target.DutyContentFinderConditionId);
        Assert.Equal(draft.DutyDisplayName, target.DutyDisplayName);
        Assert.Equal(draft.DutyUnsynced, target.DutyUnsynced);
        Assert.Equal(draft.DutyExpectedPartySize, target.DutyExpectedPartySize);
        Assert.Equal(7, target.StopPolicy.AfterRuns);
        Assert.NotSame(draft.StopPolicy, target.StopPolicy);
        Assert.NotSame(draft.CompletionActions, target.CompletionActions);
        Assert.Equal(9, target.CompletionActions!.SoundEffectId);
        Assert.Equal(now, target.UpdatedAtUtc);
    }

    [Fact]
    public void EnablingPreservesCrewAndExistingLevelingSettings()
    {
        var target = Target(DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.Trust);
        var crew = target.Slots;
        var leveling = target.LevelingMode;
        var threshold = Assert.Single(leveling.DutyThresholds);
        var draft = Draft(DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.PremadeDuty);

        var result = DadLevelingModeActivationRules.Apply(target, draft, enabled: true, UtcNow());

        Assert.True(result.Accepted);
        Assert.Same(crew, target.Slots);
        Assert.Same(leveling, target.LevelingMode);
        Assert.Equal(95, target.LevelingMode.GoalLevel);
        Assert.Equal(DadLevelingJobOrder.HighestBelowGoal, target.LevelingMode.JobOrder);
        Assert.Same(threshold, Assert.Single(target.LevelingMode.DutyThresholds));
        Assert.Equal((uint)123, threshold.ContentFinderConditionId);
    }

    [Fact]
    public void DisablingOnlyClearsEnabledFlag()
    {
        var target = Target(DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.Trust);
        target.LevelingMode.Enabled = true;
        var crew = target.Slots;
        var leveling = target.LevelingMode;
        var updated = target.UpdatedAtUtc;
        var draft = Draft(DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.PremadeDuty);

        var result = DadLevelingModeActivationRules.Apply(target, draft, enabled: false, UtcNow().AddHours(1));

        Assert.True(result.Accepted);
        Assert.False(target.LevelingMode.Enabled);
        Assert.Equal(DadPlannerRunFamily.LevelingNpc, target.RunFamily);
        Assert.Equal(DadPlannerActivityMode.Trust, target.ActivityMode);
        Assert.Same(crew, target.Slots);
        Assert.Same(leveling, target.LevelingMode);
        Assert.Equal(95, target.LevelingMode.GoalLevel);
        Assert.Equal(updated, target.UpdatedAtUtc);
    }

    [Fact]
    public void RejectedActivationDoesNotMutatePreset()
    {
        var target = Target(DadPlannerRunFamily.LevelingNpc, DadPlannerActivityMode.Trust);
        var crew = target.Slots;
        var leveling = target.LevelingMode;
        var displayName = target.DisplayName;
        var dutyId = target.DutyContentFinderConditionId;
        var updated = target.UpdatedAtUtc;
        var unsupportedDraft = Draft(DadPlannerRunFamily.DutyFinder, DadPlannerActivityMode.CustomDuty);

        var result = DadLevelingModeActivationRules.Apply(target, unsupportedDraft, enabled: true, UtcNow());

        Assert.False(result.Accepted);
        Assert.Contains("Leveling / NPC", result.Summary, StringComparison.Ordinal);
        Assert.Equal(displayName, target.DisplayName);
        Assert.Equal(DadPlannerRunFamily.LevelingNpc, target.RunFamily);
        Assert.Equal(DadPlannerActivityMode.Trust, target.ActivityMode);
        Assert.Equal(dutyId, target.DutyContentFinderConditionId);
        Assert.Same(crew, target.Slots);
        Assert.Same(leveling, target.LevelingMode);
        Assert.False(target.LevelingMode.Enabled);
        Assert.Equal(updated, target.UpdatedAtUtc);
    }

    private static DadPlannerGroup Target(DadPlannerRunFamily runFamily, DadPlannerActivityMode activityMode)
        => new()
        {
            GroupId = "target",
            DisplayName = "Saved preset",
            RunFamily = runFamily,
            ActivityMode = activityMode,
            DutyContentFinderConditionId = 42,
            DutyDisplayName = "Saved duty",
            StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 2 },
            LevelingMode = new DadLevelingModeOptions
            {
                Enabled = false,
                GoalLevel = 95,
                JobOrder = DadLevelingJobOrder.HighestBelowGoal,
                DutyThresholds =
                [
                    new DadLevelingDutyThreshold
                    {
                        MinimumLevel = 1,
                        ContentFinderConditionId = 123,
                        DutyDisplayName = "Leveling duty",
                    },
                ],
            },
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = DadPlannerSlotRules.LeaderSlotId,
                    RequiredAccountKey = new DadAccountKey("account"),
                    RequiredCharacterKey = new DadCharacterKey("character"),
                },
            ],
            UpdatedAtUtc = UtcNow().AddDays(-1),
        };

    private static DadPlannerGroup Draft(DadPlannerRunFamily runFamily, DadPlannerActivityMode activityMode)
        => new()
        {
            GroupId = "draft",
            DisplayName = "Visible draft",
            RunFamily = runFamily,
            ActivityMode = activityMode,
            StopPolicy = new DadRunStopPolicy(),
        };

    private static DateTime UtcNow()
        => new(2026, 7, 17, 15, 0, 0, DateTimeKind.Utc);
}
