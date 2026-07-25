using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAutoPartyFleetMatrixTests
{
    private static readonly DateTime FixtureTime = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FreshConfigurationKeepsFleetApplyDisabled()
    {
        var configuration = new Configuration();

        Assert.False(configuration.AutoPartyFleet.Enabled);
        Assert.Empty(configuration.AutoPartyFleet.Rows);
        Assert.Empty(configuration.AutoPartyFleet.CrewSets);
        Assert.Empty(configuration.AutoPartyFleet.Blueprints);
        Assert.Null(configuration.AutoPartyFleet.UndoSnapshot);
    }

    [Fact]
    public void PreviewIsDeterministicAndDoesNotMutateConfiguration()
    {
        var configuration = BuildConfiguration(2, 4);
        var before = DadIpcJson.Serialize(configuration.AutoPartyFleet);
        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);

        var first = service.BuildPreview(FixtureTime);
        var second = service.BuildPreview(FixtureTime.AddHours(3));

        Assert.True(first.CanApply);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.PlannerGroups.Select(static group => group.GroupId), second.PlannerGroups.Select(static group => group.GroupId));
        Assert.Equal(first.Schedules.Select(static schedule => schedule.ScheduleId), second.Schedules.Select(static schedule => schedule.ScheduleId));
        Assert.Equal(before, DadIpcJson.Serialize(configuration.AutoPartyFleet));
        Assert.Empty(configuration.PlannerGroups);
        Assert.Empty(configuration.Schedules);
    }

    [Fact]
    public void MaximumFixtureGeneratesFortyFourMemberPartiesFromOneHundredSixtyRows()
    {
        var configuration = BuildConfiguration(40, 4);
        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);

        var preview = service.BuildPreview(FixtureTime);

        Assert.True(preview.CanApply);
        Assert.Equal(160, configuration.AutoPartyFleet.Rows.Count);
        Assert.Equal(40, preview.PlannerGroups.Count);
        Assert.All(preview.PlannerGroups, static group =>
        {
            Assert.Equal(4, group.Slots.Count);
            Assert.Equal(
                [DadAllianceAssignment.A, DadAllianceAssignment.B, DadAllianceAssignment.C, DadAllianceAssignment.C],
                group.Slots.Select(static slot => slot.AllianceAssignment).ToArray());
            Assert.Equal(DadQueueAuthority.LocalOnly, group.QueueAuthority);
            Assert.Equal(DadTransportOwner.DadDirect, group.TransportOwner);
            Assert.All(group.Slots, static slot =>
            {
                Assert.NotEqual(0u, slot.RequiredJobId);
                Assert.False(slot.CharacterLoadInstruction.Enabled);
            });
        });
        var schedule = Assert.Single(preview.Schedules);
        Assert.Equal(40, schedule.Entries.Count);
        Assert.Equal(preview.PlannerGroups.Select(static group => group.GroupId), schedule.Entries.Select(static entry => entry.GroupId));
    }

    [Fact]
    public void ApplyAndDurableUndoRestoreExactPriorCollections()
    {
        var configuration = BuildConfiguration(2, 4);
        configuration.PlannerGroups.Add(BaselineGroup());
        configuration.Schedules.Add(BaselineSchedule());
        var groupsBefore = DadIpcJson.Serialize(configuration.PlannerGroups);
        var schedulesBefore = DadIpcJson.Serialize(configuration.Schedules);
        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);

        var applied = service.Apply(FixtureTime);

        Assert.True(applied.Succeeded);
        Assert.NotEmpty(applied.UndoToken);
        Assert.NotNull(configuration.AutoPartyFleet.UndoSnapshot);
        Assert.Equal(applied.UndoToken, configuration.AutoPartyFleet.UndoSnapshot!.UndoToken);
        Assert.Equal(3, configuration.PlannerGroups.Count);
        Assert.Equal(2, configuration.Schedules.Count);

        var undone = service.Undo(applied.UndoToken);

        Assert.True(undone.Succeeded);
        Assert.Equal(groupsBefore, DadIpcJson.Serialize(configuration.PlannerGroups));
        Assert.Equal(schedulesBefore, DadIpcJson.Serialize(configuration.Schedules));
        Assert.Null(configuration.AutoPartyFleet.UndoSnapshot);
        Assert.Empty(configuration.AutoPartyFleet.ManagedPlannerGroupIds);
        Assert.Empty(configuration.AutoPartyFleet.ManagedScheduleIds);
    }

    [Fact]
    public void ApplyRollsBackWholeRevisionWhenSaveBoundaryFails()
    {
        var configuration = BuildConfiguration(1, 4);
        configuration.PlannerGroups.Add(BaselineGroup());
        configuration.Schedules.Add(BaselineSchedule());
        var groupsBefore = DadIpcJson.Serialize(configuration.PlannerGroups);
        var schedulesBefore = DadIpcJson.Serialize(configuration.Schedules);
        var matrixBefore = DadIpcJson.Serialize(configuration.AutoPartyFleet);
        var service = new DadAutoPartyFleetMatrixService(
            configuration,
            static () => string.Empty,
            static () => throw new IOException("synthetic durable-save rejection"));

        var result = service.Apply(FixtureTime);

        Assert.False(result.Succeeded);
        Assert.Equal("dad-fleet-save-failed", result.SafeCode);
        Assert.Equal(groupsBefore, DadIpcJson.Serialize(configuration.PlannerGroups));
        Assert.Equal(schedulesBefore, DadIpcJson.Serialize(configuration.Schedules));
        Assert.Equal(matrixBefore, DadIpcJson.Serialize(configuration.AutoPartyFleet));
    }

    [Fact]
    public void ActiveWorkBlocksApplyImportAndUndo()
    {
        var configuration = BuildConfiguration(1, 4);
        var unlocked = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);
        var applied = unlocked.Apply(FixtureTime);
        Assert.True(applied.Succeeded);
        var locked = new DadAutoPartyFleetMatrixService(configuration, static () => "A DAD run is active.");

        Assert.Equal("dad-fleet-mutation-locked", locked.Apply(FixtureTime).SafeCode);
        Assert.Equal("dad-fleet-mutation-locked", locked.Undo(applied.UndoToken).SafeCode);
        Assert.Equal("dad-fleet-mutation-locked", locked.ImportTsv(unlocked.ExportTsv()).SafeCode);
    }

    [Fact]
    public void DraftMutationsHonorLockAndRollbackSaveFailure()
    {
        var configuration = BuildConfiguration(1, 4);
        var originalBlueprintId = Assert.Single(configuration.AutoPartyFleet.Blueprints).BlueprintId;
        var locked = new DadAutoPartyFleetMatrixService(configuration, static () => "A Schedule run is active.");

        Assert.Equal("dad-fleet-mutation-locked", locked.SetEnabled(false).SafeCode);
        Assert.Equal("dad-fleet-mutation-locked", locked.RemoveBlueprint(originalBlueprintId).SafeCode);
        Assert.True(configuration.AutoPartyFleet.Enabled);
        Assert.Single(configuration.AutoPartyFleet.Blueprints);

        var failing = new DadAutoPartyFleetMatrixService(
            configuration,
            static () => string.Empty,
            static () => throw new IOException("synthetic durable-save rejection"));
        Assert.Equal("dad-fleet-enable-save-failed", failing.SetEnabled(false).SafeCode);
        Assert.True(configuration.AutoPartyFleet.Enabled);
        Assert.Equal("dad-fleet-blueprint-save-failed", failing.AddBlueprint(new DadAutoPartyFleetBlueprint
        {
            BlueprintId = "new-blueprint",
            DisplayName = "New Blueprint",
            CrewSetIds = ["crew-00"],
            DutyContentFinderConditionId = 2,
        }).SafeCode);
        Assert.Single(configuration.AutoPartyFleet.Blueprints);
    }

    [Fact]
    public void ApplyRefusesToOverwriteUnownedDeterministicId()
    {
        var configuration = BuildConfiguration(1, 4);
        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);
        var preview = service.BuildPreview(FixtureTime);
        configuration.PlannerGroups.Add(new DadPlannerGroup { GroupId = preview.PlannerGroups[0].GroupId, DisplayName = "Unowned" });

        var result = service.Apply(FixtureTime);

        Assert.False(result.Succeeded);
        Assert.Equal("dad-fleet-unowned-id-collision", result.SafeCode);
        Assert.Single(configuration.PlannerGroups);
    }

    [Fact]
    public void RemoteRowsGenerateOpaquePlaceholdersWithoutLoadCommands()
    {
        var configuration = BuildConfiguration(1, 1);
        var row = configuration.AutoPartyFleet.Rows[0];
        row.IsRemote = true;
        row.OpaqueCharacterId = "remote-opaque-001";
        row.AccountKey = string.Empty;
        row.CharacterKey = string.Empty;
        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);

        var slot = Assert.Single(Assert.Single(service.BuildPreview(FixtureTime).PlannerGroups).Slots);

        Assert.True(slot.RequiredAccountKey.IsEmpty);
        Assert.True(slot.RequiredCharacterKey.IsEmpty);
        Assert.Equal("remote-opaque-001", slot.SharedIdentity?.IdentityToken);
        Assert.False(slot.CharacterLoadInstruction.Enabled);
    }

    [Fact]
    public void TsvRoundTripPreservesRowsAndCrewOrderAndProtectsFormulaPrefixes()
    {
        var configuration = BuildConfiguration(1, 4);
        configuration.AutoPartyFleet.Rows[0].OpaqueCharacterId = "=opaque-fixture";

        var exported = DadAutoPartyFleetTsv.Export(configuration.AutoPartyFleet);
        var parsed = DadAutoPartyFleetTsv.Parse(exported);

        Assert.Contains("'=opaque-fixture", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("account-000", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("character-000", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("account_key", DadAutoPartyFleetTsv.Header, StringComparison.Ordinal);
        Assert.True(parsed.Succeeded);
        Assert.Equal(4, parsed.Draft!.Rows.Count);
        Assert.Equal("=opaque-fixture", parsed.Draft.Rows[0].OpaqueCharacterId);
        Assert.Equal(new[] { "row-000", "row-001", "row-002", "row-003" }, Assert.Single(parsed.Draft.CrewSets).FleetRowIds);

        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);
        Assert.True(service.ImportTsv(exported).Succeeded);
        Assert.Equal("account-000", configuration.AutoPartyFleet.Rows[0].AccountKey);
        Assert.Equal("character-000", configuration.AutoPartyFleet.Rows[0].CharacterKey);
    }

    [Fact]
    public void LocalRosterMergeCreatesPrivateBindingsThatPortableTsvOmits()
    {
        var configuration = new Configuration();
        var service = new DadAutoPartyFleetMatrixService(configuration, static () => string.Empty);
        var characters = new[]
        {
            new DadAcquiredCharacter
            {
                CharacterKey = "private-character-key",
                AccountId = "private-account-key",
                CurrentJobId = 19,
            },
        };

        var result = service.MergeLocalRoster(characters);

        Assert.True(result.Succeeded);
        var row = Assert.Single(configuration.AutoPartyFleet.Rows);
        Assert.Equal("private-character-key", row.CharacterKey);
        Assert.Equal("private-account-key", row.AccountKey);
        var exported = service.ExportTsv();
        Assert.DoesNotContain("private-character-key", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account-key", exported, StringComparison.Ordinal);
        Assert.Contains(row.RowId, exported, StringComparison.Ordinal);
        Assert.Contains(row.OpaqueCharacterId, exported, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@unsafe")]
    [InlineData("=unsafe")]
    [InlineData("+unsafe")]
    [InlineData("-unsafe")]
    public void TsvRejectsUnescapedSpreadsheetFormulaPrefixes(string rowId)
    {
        var line = string.Join('\t', rowId, string.Empty, "Dps", "1", "false", "true", "crew", "Crew", "1");

        var result = DadAutoPartyFleetTsv.Parse(DadAutoPartyFleetTsv.Header + "\r\n" + line + "\r\n");

        Assert.False(result.Succeeded);
        Assert.Equal("dad-fleet-tsv-formula-prefix", result.SafeCode);
    }

    [Fact]
    public void TsvRejectsDuplicateRowsAndUnsupportedControlCharacters()
    {
        const string line = "row\t\tDps\t1\tfalse\ttrue\tcrew\tCrew\t1";
        var duplicate = DadAutoPartyFleetTsv.Parse(DadAutoPartyFleetTsv.Header + "\r\n" + line + "\r\n" + line + "\r\n");
        var control = DadAutoPartyFleetTsv.Parse(DadAutoPartyFleetTsv.Header + "\r\n" + line + "\u0001\r\n");

        Assert.Equal("dad-fleet-tsv-row-id-duplicate", duplicate.SafeCode);
        Assert.Equal("dad-fleet-tsv-control-character", control.SafeCode);
    }

    private static Configuration BuildConfiguration(int crewCount, int membersPerCrew)
    {
        var configuration = new Configuration
        {
            AutoPartyFleet = new DadAutoPartyFleetConfiguration { Enabled = true },
        };
        for (var crewIndex = 0; crewIndex < crewCount; crewIndex++)
        {
            var crew = new DadAutoPartyCrewSet
            {
                CrewSetId = $"crew-{crewIndex:D2}",
                DisplayName = $"Crew {crewIndex:D2}",
            };
            for (var memberIndex = 0; memberIndex < membersPerCrew; memberIndex++)
            {
                var rowIndex = crewIndex * membersPerCrew + memberIndex;
                var rowId = $"row-{rowIndex:D3}";
                configuration.AutoPartyFleet.Rows.Add(new DadAutoPartyFleetRow
                {
                    RowId = rowId,
                    OpaqueCharacterId = $"opaque-{rowIndex:D3}",
                    AccountKey = $"account-{rowIndex:D3}",
                    CharacterKey = $"character-{rowIndex:D3}",
                    AllianceAssignment = memberIndex switch
                    {
                        0 => DadAllianceAssignment.A,
                        1 => DadAllianceAssignment.B,
                        _ => DadAllianceAssignment.C,
                    },
                    Role = memberIndex switch
                    {
                        0 => DadPartyRole.Tank,
                        1 => DadPartyRole.Healer,
                        _ => DadPartyRole.Dps,
                    },
                    JobId = checked((uint)(memberIndex + 1)),
                    Enabled = true,
                });
                crew.FleetRowIds.Add(rowId);
            }
            configuration.AutoPartyFleet.CrewSets.Add(crew);
        }
        configuration.AutoPartyFleet.Blueprints.Add(new DadAutoPartyFleetBlueprint
        {
            BlueprintId = "blueprint-duty",
            DisplayName = "Synthetic Duty",
            CrewSetIds = configuration.AutoPartyFleet.CrewSets.Select(static crew => crew.CrewSetId).ToList(),
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ActivityMode = DadPlannerActivityMode.PremadeDuty,
            DutyContentFinderConditionId = 1,
            DutyDisplayName = "Synthetic Fixture",
            CreateSchedule = true,
            ScheduleCadence = DadScheduleCadence.Manual,
            RepeatCount = 2,
        });
        return configuration;
    }

    private static DadPlannerGroup BaselineGroup()
        => new()
        {
            GroupId = "baseline-plan",
            DisplayName = "Baseline Plan",
            DutyContentFinderConditionId = 2,
            DutyDisplayName = "Baseline Fixture",
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    RequiredAccountKey = new DadAccountKey("baseline-account"),
                    RequiredCharacterKey = new DadCharacterKey("baseline-character"),
                    RequiredJobId = 1,
                },
            ],
            CreatedAtUtc = FixtureTime.AddDays(-1),
            UpdatedAtUtc = FixtureTime.AddHours(-1),
        };

    private static DadScheduleDefinition BaselineSchedule()
        => new()
        {
            ScheduleId = "baseline-schedule",
            DisplayName = "Baseline Schedule",
            Entries =
            [
                new DadScheduleEntry
                {
                    EntryId = "baseline-entry",
                    GroupId = "baseline-plan",
                    PresetName = "Baseline Plan",
                    RepeatCount = 3,
                    CreatedAtUtc = FixtureTime.AddDays(-1),
                    UpdatedAtUtc = FixtureTime.AddHours(-1),
                },
            ],
            CreatedAtUtc = FixtureTime.AddDays(-1),
            UpdatedAtUtc = FixtureTime.AddHours(-1),
        };
}
