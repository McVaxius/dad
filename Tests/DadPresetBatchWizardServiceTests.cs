using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPresetBatchWizardServiceTests
{
    private static readonly DateTime PreviewTime = new(2026, 7, 19, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Synthetic15915944FixtureBuilds158Crews316PlansAndExpectedSchedules()
    {
        var fixture = BuildSyntheticFixture();
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });

        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);

        Assert.True(preview.CanApply, string.Join(Environment.NewLine, preview.Issues.Select(static issue => issue.Message)));
        Assert.Equal(158, preview.Crews.Count);
        Assert.Equal(316, preview.PlannerGroups.Count);
        Assert.Equal(3, preview.Schedules.Count);
        Assert.Equal([158, 158, 316], preview.Schedules.Select(static schedule => schedule.Entries.Count).ToArray());
        Assert.Equal(2, preview.UnusedCounts.Sum(static count => count.UnusedCount));
        Assert.All(preview.PlannerGroups, static plan => Assert.False(plan.IsTemplate));
        Assert.All(preview.PlannerGroups, static plan => Assert.False(plan.ScheduleEnabled));
        Assert.All(preview.PlannerGroups, static plan => Assert.True(string.IsNullOrEmpty(plan.AutoPartyProposalId)));
        Assert.Equal(2, fixture.Configuration.PlannerGroups.Count);
        Assert.All(fixture.Configuration.PlannerGroups, static template =>
            Assert.All(template.Slots, static slot => Assert.True(slot.RequiredCharacterKey.IsEmpty)));

        var msqPlans = preview.PlannerGroups.Take(158).ToList();
        var levelingPlans = preview.PlannerGroups.Skip(158).Take(158).ToList();
        Assert.All(msqPlans.SelectMany(static plan => DadPlannerSlotRules.GetPrimaryRows(plan.Slots)),
            static slot => Assert.True(slot.SkipIfDailyRouletteRewardReceived));
        Assert.All(levelingPlans.SelectMany(static plan => DadPlannerSlotRules.GetPrimaryRows(plan.Slots)),
            static slot => Assert.False(slot.SkipIfDailyRouletteRewardReceived));
        Assert.All(preview.PlannerGroups, static plan => Assert.Equal(
            [DadAllianceAssignment.A, DadAllianceAssignment.B, DadAllianceAssignment.C, DadAllianceAssignment.C],
            DadPlannerSlotRules.GetPrimaryRows(plan.Slots)
                .Select(static slot => slot.AllianceAssignment)
                .ToArray()));
        Assert.Equal(DadScheduleCadence.DailyReset, preview.Schedules[0].Cadence);
        Assert.Equal(DadScheduleCadence.Manual, preview.Schedules[1].Cadence);
        Assert.Equal(DadScheduleCadence.DailyReset, preview.Schedules[2].Cadence);
    }

    [Fact]
    public void GenerationIdsAndOrderingAreStableAcrossPreviewRefreshes()
    {
        var fixture = BuildSyntheticFixture();
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });

        var first = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);
        var second = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime.AddMinutes(5));

        Assert.True(first.CanApply);
        Assert.True(second.CanApply);
        Assert.Equal(
            first.PlannerGroups.Select(static plan => plan.GroupId),
            second.PlannerGroups.Select(static plan => plan.GroupId));
        Assert.Equal(
            first.PlannerGroups.Select(static plan => plan.DisplayName),
            second.PlannerGroups.Select(static plan => plan.DisplayName));
        Assert.Equal(
            first.Schedules.Select(static schedule => schedule.ScheduleId),
            second.Schedules.Select(static schedule => schedule.ScheduleId));
        Assert.Equal(
            first.Schedules[2].Entries.Select(static entry => entry.GroupId),
            second.Schedules[2].Entries.Select(static entry => entry.GroupId));
        Assert.Equal("MSQ Dynamis 01", first.PlannerGroups[0].DisplayName);
        Assert.Equal("MSQ OCE 39", first.PlannerGroups[157].DisplayName);
        Assert.Equal("Leveling Dynamis 01", first.PlannerGroups[158].DisplayName);
    }

    [Fact]
    public void DisabledAllDailyOptionPreservesTemplateDailyFlags()
    {
        var fixture = BuildSyntheticFixture();
        fixture.Configuration.PlannerGroups[1].Slots[0].SkipIfDailyRouletteRewardReceived = true;
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });

        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);
        var generatedRows = DadPlannerSlotRules.GetPrimaryRows(preview.PlannerGroups[158].Slots);

        Assert.True(preview.CanApply);
        Assert.True(generatedRows[0].SkipIfDailyRouletteRewardReceived);
        Assert.All(generatedRows.Skip(1), static row => Assert.False(row.SkipIfDailyRouletteRewardReceived));
        Assert.True(fixture.Configuration.PlannerGroups[1].Slots[0].SkipIfDailyRouletteRewardReceived);
    }

    [Fact]
    public void PreviewDoesNotNormalizeNullCollectionsIntoConfiguration()
    {
        var configuration = new Configuration
        {
            PlannerGroups = null!,
            Schedules = null!,
        };
        var service = new DadPresetBatchWizardService(configuration, static () => string.Empty, static () => { });

        var preview = service.BuildPreview(new DadPresetBatchDraft(), new DadAccountRosterCatalog(), PreviewTime);

        Assert.False(preview.CanApply);
        Assert.Null(configuration.PlannerGroups);
        Assert.Null(configuration.Schedules);
    }

    [Fact]
    public void CombinedScheduleUsesPoolThenCrewThenTemplateOrder()
    {
        var fixture = BuildSyntheticFixture();
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });

        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);
        var combined = preview.Schedules.Single(static schedule => schedule.DisplayName == "MSQ + Leveling Combined");

        Assert.Equal(preview.PlannerGroups[0].GroupId, combined.Entries[0].GroupId);
        Assert.Equal(preview.PlannerGroups[158].GroupId, combined.Entries[1].GroupId);
        Assert.Equal(preview.PlannerGroups[1].GroupId, combined.Entries[2].GroupId);
        Assert.Equal(preview.PlannerGroups[159].GroupId, combined.Entries[3].GroupId);
    }

    [Fact]
    public void ApplyAppendsAtomicallyAndExactSessionUndoRestoresOriginalCollections()
    {
        var fixture = BuildSyntheticFixture();
        var saves = 0;
        var service = new DadPresetBatchWizardService(
            fixture.Configuration,
            static () => string.Empty,
            () => saves++);
        var originalIds = fixture.Configuration.PlannerGroups.Select(static group => group.GroupId).ToArray();
        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);

        var applied = service.Apply(preview);

        Assert.True(applied.Succeeded);
        Assert.Equal(318, fixture.Configuration.PlannerGroups.Count);
        Assert.Equal(3, fixture.Configuration.Schedules.Count);
        Assert.True(service.CanUndo);

        var undone = service.Undo(applied.UndoToken);

        Assert.True(undone.Succeeded);
        Assert.Equal(originalIds, fixture.Configuration.PlannerGroups.Select(static group => group.GroupId).ToArray());
        Assert.Empty(fixture.Configuration.Schedules);
        Assert.False(service.CanUndo);
        Assert.Equal(2, saves);
    }

    [Fact]
    public void UndoRefusesAnyPostApplyPlannerOrScheduleDrift()
    {
        var fixture = BuildSyntheticFixture();
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });
        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);
        var applied = service.Apply(preview);
        fixture.Configuration.PlannerGroups.Add(new DadPlannerGroup { GroupId = "newer-user-plan", DisplayName = "Newer user Plan" });

        var undone = service.Undo(applied.UndoToken);

        Assert.False(undone.Succeeded);
        Assert.Equal("dad-batch-undo-drift", undone.SafeCode);
        Assert.Contains(fixture.Configuration.PlannerGroups, static group => group.GroupId == "newer-user-plan");
        Assert.True(service.CanUndo);
    }

    [Fact]
    public void StaleOrChangedPreviewCannotApply()
    {
        var fixture = BuildSyntheticFixture();
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });
        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);
        fixture.Configuration.Schedules.Add(new DadScheduleDefinition { ScheduleId = "newer-schedule", DisplayName = "Newer Schedule" });

        var stale = service.Apply(preview);

        Assert.False(stale.Succeeded);
        Assert.Equal("dad-batch-preview-stale", stale.SafeCode);
        Assert.Equal(2, fixture.Configuration.PlannerGroups.Count);
    }

    [Fact]
    public void InvalidPoolOverlapTemplateShapeAndDailyFlagCombinationBlockPreview()
    {
        var fixture = BuildSyntheticFixture();
        fixture.Draft.Pools[1].DataCenterIds.Add(fixture.Draft.Pools[0].DataCenterIds[0]);
        fixture.Draft.Templates[0].ScheduleCadence = DadScheduleCadence.Manual;
        fixture.Configuration.PlannerGroups[0].Slots.Add(new DadPlannerGroupSlot
        {
            SlotId = "Slot1",
            IsSubstitute = true,
        });
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });

        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Issues, static issue => issue.SafeCode == "dad-batch-pool-dc-overlap");
        Assert.Contains(preview.Issues, static issue => issue.SafeCode == "dad-batch-template-substitutes");
        Assert.Contains(preview.Issues, static issue => issue.SafeCode == "dad-batch-daily-flags-require-daily");
    }

    [Fact]
    public void AnchorOutsidePoolIsAVisibleNonBlockingWarning()
    {
        var fixture = BuildSyntheticFixture();
        var dynamisAnchor = fixture.Draft.AnchorLanes[0].Assignments[0].Character.Clone();
        fixture.Draft.AnchorLanes[0].Assignments[1].Character = dynamisAnchor;
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });

        var preview = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);

        Assert.True(preview.CanApply);
        Assert.Contains(preview.Issues, static issue =>
            issue.SafeCode == "dad-batch-anchor-outside-pool" && !issue.IsBlocking);
    }

    [Fact]
    public void ExistingNameOrDeterministicIdCollisionBlocksSecondGeneration()
    {
        var fixture = BuildSyntheticFixture();
        var service = new DadPresetBatchWizardService(fixture.Configuration, static () => string.Empty, static () => { });
        var first = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);
        Assert.True(service.Apply(first).Succeeded);

        var second = service.BuildPreview(fixture.Draft, fixture.Catalog, PreviewTime);

        Assert.False(second.CanApply);
        Assert.Contains(second.Issues, static issue =>
            issue.SafeCode is "dad-batch-plan-id-collision" or "dad-batch-plan-name-collision");
    }

    private static Fixture BuildSyntheticFixture()
    {
        var configuration = new Configuration();
        configuration.PlannerGroups =
        [
            Template("template-msq", "Daily MSQ Template"),
            Template("template-leveling", "Daily Leveling Template"),
        ];
        configuration.Schedules = [];

        var catalog = new DadAccountRosterCatalog
        {
            IsFullRosterAvailable = true,
            Characters = [],
        };
        var rotatingOne = AddRotatingAccount(catalog, "rotating-one", 1_000_000);
        var rotatingTwo = AddRotatingAccount(catalog, "rotating-two", 2_000_000);
        var anchorOne = AddAnchorAccount(catalog, "anchor-one", 3_000_000);
        var anchorTwo = AddAnchorAccount(catalog, "anchor-two", 4_000_000);

        var pools = new List<DadPresetBatchPool>
        {
            new() { PoolId = "dynamis", DisplayName = "Dynamis", DataCenterIds = [101], CrewCount = 40 },
            new() { PoolId = "eu", DisplayName = "EU", DataCenterIds = [102], CrewCount = 39 },
            new() { PoolId = "jp", DisplayName = "JP", DataCenterIds = [103], CrewCount = 40 },
            new() { PoolId = "oce", DisplayName = "OCE", DataCenterIds = [104], CrewCount = 39 },
        };
        var draft = new DadPresetBatchDraft
        {
            RotatingLanes =
            [
                new() { AccountKey = new DadAccountKey("rotating-one"), Characters = rotatingOne },
                new() { AccountKey = new DadAccountKey("rotating-two"), Characters = rotatingTwo },
            ],
            AnchorLanes =
            [
                AnchorLane("anchor-one", anchorOne, pools),
                AnchorLane("anchor-two", anchorTwo, pools),
            ],
            Pools = pools,
            Templates =
            [
                new DadPresetBatchTemplate
                {
                    PlannerGroupId = "template-msq",
                    ActivityLabel = "MSQ",
                    PlanNameFormat = "{Activity} {Pool} {Index:00}",
                    ScheduleName = "Daily MSQ Batch",
                    ScheduleCadence = DadScheduleCadence.DailyReset,
                    SetDailyRewardChecksForAllPrimary = true,
                },
                new DadPresetBatchTemplate
                {
                    PlannerGroupId = "template-leveling",
                    ActivityLabel = "Leveling",
                    PlanNameFormat = "{Activity} {Pool} {Index:00}",
                    ScheduleName = "Daily Leveling Batch",
                    ScheduleCadence = DadScheduleCadence.Manual,
                },
            ],
            CreateCombinedSchedule = true,
            CombinedScheduleName = "MSQ + Leveling Combined",
            CombinedScheduleCadence = DadScheduleCadence.DailyReset,
        };
        return new Fixture(configuration, catalog, draft);
    }

    private static DadPlannerGroup Template(string id, string name)
        => new()
        {
            GroupId = id,
            DisplayName = name,
            Slots = Enumerable.Range(1, 4).Select(index => new DadPlannerGroupSlot
            {
                SlotId = DadPlannerSlotRules.FormatSlotId(index),
                AllianceAssignment = index switch
                {
                    1 => DadAllianceAssignment.A,
                    2 => DadAllianceAssignment.B,
                    _ => DadAllianceAssignment.C,
                },
                RequiredRole = DadPartyRole.Any,
                RequiredAccountKey = new DadAccountKey(string.Empty),
                RequiredCharacterKey = new DadCharacterKey(string.Empty),
                AllowSubstitution = false,
            }).ToList(),
        };

    private static List<DadRosterCharacterRef> AddRotatingAccount(
        DadAccountRosterCatalog catalog,
        string account,
        ulong contentIdBase)
    {
        var counts = new[] { 41, 39, 40, 39 };
        var selected = new List<DadRosterCharacterRef>();
        var ordinal = 0;
        for (var pool = 0; pool < counts.Length; pool++)
        {
            for (var index = 0; index < counts[pool]; index++)
            {
                ordinal++;
                var character = Character(
                    account,
                    $"{account} {ordinal:000}",
                    contentIdBase + (ulong)ordinal,
                    checked((uint)(101 + pool)),
                    $"World {pool + 1}");
                catalog.Characters.Add(character);
                selected.Add(DadRosterIdentity.From(character));
            }
        }
        return selected;
    }

    private static List<DadRosterCharacterRef> AddAnchorAccount(
        DadAccountRosterCatalog catalog,
        string account,
        ulong contentIdBase)
    {
        var selected = new List<DadRosterCharacterRef>();
        for (var pool = 0; pool < 4; pool++)
        {
            var character = Character(
                account,
                $"{account} anchor {pool + 1}",
                contentIdBase + checked((ulong)pool + 1),
                checked((uint)(101 + pool)),
                $"World {pool + 1}");
            catalog.Characters.Add(character);
            selected.Add(DadRosterIdentity.From(character));
        }
        return selected;
    }

    private static DadPresetBatchAnchorLane AnchorLane(
        string account,
        IReadOnlyList<DadRosterCharacterRef> characters,
        IReadOnlyList<DadPresetBatchPool> pools)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            Assignments = pools.Select((pool, index) => new DadPresetBatchAnchorAssignment
            {
                PoolId = pool.PoolId,
                Character = characters[index].Clone(),
            }).ToList(),
        };

    private static DadRosterCharacter Character(
        string account,
        string name,
        ulong contentId,
        uint dataCenterId,
        string world)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            CharacterKey = new DadCharacterKey($"{name}@{world}"),
            ContentId = contentId,
            CharacterName = name,
            WorldId = dataCenterId * 10,
            WorldName = world,
            DataCenterId = dataCenterId,
            DataCenterName = $"DC {dataCenterId}",
            Visibility = DadRosterVisibility.Active,
        };

    private sealed record Fixture(
        Configuration Configuration,
        DadAccountRosterCatalog Catalog,
        DadPresetBatchDraft Draft);
}
