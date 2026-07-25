using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadShareServiceTests
{
    private const string PlanA = "11111111111111111111111111111111";
    private const string PlanB = "22222222222222222222222222222222";
    private const string PlanC = "33333333333333333333333333333333";
    private const string ScheduleA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EntryA = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string EntryB = "cccccccccccccccccccccccccccccccc";
    private const string EntryC = "dddddddddddddddddddddddddddddddd";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PlanRoundTripPreservesShareableFieldsAndExcludesMachineLocalState()
    {
        var service = CreateService();
        var source = BuildPlan(PlanA, "Alice Example Plan for Primary Account");
        var originalCreated = source.CreatedAtUtc;
        var commands = source.CompletionActions!.Commands.ToArray();

        Assert.True(service.TryExportPlan(source, KnownIdentities(), out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);
        var transfer = Assert.IsType<DadSharePlanDto>(envelope!.Plan);

        Assert.Equal(PlanA, transfer.GroupId);
        Assert.Equal(source.ActivityMode, transfer.ActivityMode);
        Assert.Equal(source.RunFamily, transfer.RunFamily);
        Assert.Equal(source.RouletteTarget.RouletteId, transfer.RouletteTarget.RouletteId);
        Assert.Equal(source.StopPolicy.Mode, transfer.StopPolicy.Mode);
        Assert.True(transfer.LevelingMode.Enabled);
        Assert.Equal(source.LevelingMode.GoalLevel, transfer.LevelingMode.GoalLevel);
        Assert.Equal(source.LevelingMode.JobOrder, transfer.LevelingMode.JobOrder);
        Assert.Equal(
            source.LevelingMode.DutyThresholds.Select(static row => (row.MinimumLevel, row.ContentFinderConditionId)),
            transfer.LevelingMode.DutyThresholds.Select(static row => (row.MinimumLevel, row.ContentFinderConditionId)));
        Assert.Equal(source.MapMode, transfer.MapMode);
        Assert.Equal(source.Slots.Select(static slot => slot.SkipIfDailyRouletteRewardReceived), transfer.Slots.Select(static slot => slot.SkipIfDailyRouletteRewardReceived));
        Assert.Equal(commands, transfer.CompletionActions!.Commands);
        Assert.Equal(source.CompletionActions.Utilities.GrandCompanyHandInCommand, transfer.CompletionActions.Utilities.GrandCompanyHandInCommand);
        Assert.DoesNotContain("Alice Example", transfer.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Primary Account", transfer.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(transfer.Slots[0].AccountToken, transfer.Slots[1].AccountToken);
        Assert.NotEqual(transfer.Slots[0].CharacterToken, transfer.Slots[1].CharacterToken);
        Assert.Equal(transfer.Slots[0].CharacterToken, transfer.StopPolicy.TargetCharacterToken);
        var preservedCompletion = transfer.CompletionActions;
        transfer.CompletionActions = null;
        var privacyAuditedJson = JsonSerializer.Serialize(transfer, JsonOptions);
        transfer.CompletionActions = preservedCompletion;
        Assert.DoesNotContain("account-real", privacyAuditedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Primary Account", privacyAuditedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice Example", privacyAuditedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bob Example", privacyAuditedJson, StringComparison.OrdinalIgnoreCase);

        var json = DecodeJson(encoded);
        Assert.DoesNotContain("launchProfileId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("characterLoadInstruction", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAtUtc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedAtUtc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scheduleEnabled", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nextEligibleTimeUtc", json, StringComparison.OrdinalIgnoreCase);

        var applied = service.Apply(envelope, [], []);
        Assert.True(applied.Success, applied.Summary);
        var imported = Assert.Single(applied.PlannerGroups);
        Assert.Equal(transfer.DisplayName, imported.DisplayName);
        Assert.Equal(commands, imported.CompletionActions!.Commands);
        Assert.NotEqual(originalCreated, imported.CreatedAtUtc);
        Assert.All(imported.Slots, slot =>
        {
            Assert.True(slot.RequiredAccountKey.IsEmpty);
            Assert.True(slot.RequiredCharacterKey.IsEmpty);
            Assert.NotNull(slot.SharedIdentity);
            Assert.Equal(string.Empty, slot.LaunchProfileId);
            Assert.False(slot.CharacterLoadInstruction.Enabled);
        });
        Assert.Equal(
            [DadAllianceAssignment.A, DadAllianceAssignment.B, DadAllianceAssignment.C],
            imported.Slots.Select(static slot => slot.AllianceAssignment).ToArray());
        Assert.True(DadSharedPlanRules.HasUnresolvedPlaceholders(imported));
        Assert.True(imported.Slots[0].SkipIfDailyRouletteRewardReceived);
        Assert.False(imported.Slots[1].SkipIfDailyRouletteRewardReceived);
        Assert.True(imported.LevelingMode.Enabled);
        Assert.Equal(DadLevelingJobOrder.HighestBelowGoal, imported.LevelingMode.JobOrder);
        Assert.Equal((uint)777, Assert.Single(imported.LevelingMode.DutyThresholds).ContentFinderConditionId);
    }

    [Fact]
    public void SchemaThreeExportsAllianceAssignmentsWhileOlderSchemasDefaultToNone()
    {
        var service = CreateService();
        var source = BuildPlan(PlanA, "Plan");
        Assert.True(service.TryExportPlan(source, KnownIdentities(), out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var current, out error), error);
        Assert.Equal(3, current!.Schema);
        Assert.True(current.Plan!.Slots[0].SkipIfDailyRouletteRewardReceived);
        Assert.Equal(DadAllianceAssignment.A, current.Plan.Slots[0].AllianceAssignment);
        Assert.Equal(DadAllianceAssignment.B, current.Plan.Slots[1].AllianceAssignment);
        Assert.Equal(DadAllianceAssignment.C, current.Plan.Slots[2].AllianceAssignment);

        var legacy = JsonNode.Parse(DecodeJson(encoded))!.AsObject();
        legacy["schema"] = 2;
        foreach (var slot in legacy["plan"]!["slots"]!.AsArray())
            slot!.AsObject().Remove("allianceAssignment");
        var legacyEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(legacy.ToJsonString()));

        Assert.True(service.TryDecode(legacyEncoded, DadShareConstants.PlanKind, out var decoded, out error), error);
        var imported = Assert.Single(service.Apply(decoded!, [], []).PlannerGroups);
        Assert.All(imported.Slots, static slot => Assert.Equal(DadAllianceAssignment.None, slot.AllianceAssignment));
        Assert.False(DadAlliancePartyFinderRules.ValidateSavedRows(imported.Slots).IsValid);
        Assert.True(imported.Slots[0].SkipIfDailyRouletteRewardReceived);
        Assert.False(imported.Slots[1].SkipIfDailyRouletteRewardReceived);
        Assert.True(imported.LevelingMode.Enabled);
        Assert.Equal(DadLevelingJobOrder.HighestBelowGoal, imported.LevelingMode.JobOrder);
        Assert.Equal((uint)777, Assert.Single(imported.LevelingMode.DutyThresholds).ContentFinderConditionId);
    }

    [Fact]
    public void ScheduleExportDeduplicatesPlansButPreservesEntryOrderAndRepeats()
    {
        var service = CreateService();
        var first = BuildPlan(PlanA, "First");
        var second = BuildPlan(PlanB, "Second");
        var schedule = new DadScheduleDefinition
        {
            ScheduleId = ScheduleA,
            DisplayName = "Alice Example schedule for Primary Account",
            Cadence = DadScheduleCadence.DailyReset,
            LastRunStartedAtUtc = DateTime.UtcNow.AddHours(-2),
            LastRunCompletedAtUtc = DateTime.UtcNow.AddHours(-1),
            LastRunStatus = DadScheduleRunStatus.Completed,
            LastSummary = "private run history",
            Entries =
            [
                Entry(EntryA, PlanA, "Alice Example first", 2),
                Entry(EntryB, PlanB, "Second", 1),
                Entry(EntryC, PlanA, "First again", 3),
            ],
        };

        Assert.True(service.TryExportSchedule(schedule, [second, first], KnownIdentities(), out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out var envelope, out error), error);

        Assert.Equal([PlanA, PlanB], envelope!.Plans.Select(static plan => plan.GroupId));
        Assert.Equal([PlanA, PlanB, PlanA], envelope.Schedule!.Entries.Select(static entry => entry.GroupId));
        Assert.Equal([2, 1, 3], envelope.Schedule.Entries.Select(static entry => entry.RepeatCount));
        Assert.Equal([EntryA, EntryB, EntryC], envelope.Schedule.Entries.Select(static entry => entry.EntryId));
        Assert.Equal(DadScheduleCadence.DailyReset, envelope.Schedule.Cadence);
        Assert.DoesNotContain("Alice Example", envelope.Schedule.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Primary Account", envelope.Schedule.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice Example", envelope.Schedule.Entries[0].PresetName, StringComparison.OrdinalIgnoreCase);
        var json = DecodeJson(encoded);
        Assert.DoesNotContain("lastRun", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private run history", json, StringComparison.OrdinalIgnoreCase);

        var applied = service.Apply(envelope, [], []);
        Assert.True(applied.Success, applied.Summary);
        Assert.Equal(2, applied.PlannerGroups.Count);
        var imported = Assert.Single(applied.Schedules);
        Assert.Equal([PlanA, PlanB, PlanA], imported.Entries.Select(static entry => entry.GroupId));
        Assert.Equal(string.Empty, imported.LastSummary);

        var existing = new DadScheduleDefinition
        {
            ScheduleId = ScheduleA,
            DisplayName = "Old",
            LastRunStatus = DadScheduleRunStatus.Blocked,
            LastSummary = "keep local history",
        };
        var replaced = service.Apply(envelope, [], [existing]);
        Assert.True(replaced.Success, replaced.Summary);
        Assert.Equal("keep local history", Assert.Single(replaced.Schedules).LastSummary);
        Assert.Equal(DadScheduleRunStatus.Blocked, replaced.Schedules[0].LastRunStatus);
    }

    [Fact]
    public void ExportForcesAnonymousTokensAndKranglingWithoutChangingSourceOrWritingLocalKeys()
    {
        var service = CreateService();
        var source = BuildPlan(PlanA, "Alice Example and Primary Account");
        var originalAccount = source.Slots[0].RequiredAccountKey;
        var originalCharacter = source.Slots[0].RequiredCharacterKey;

        Assert.True(service.TryExportPlan(source, KnownIdentities(), out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);
        var transfer = envelope!.Plan!;

        Assert.All(transfer.Slots, slot => Assert.StartsWith("acct-", slot.AccountToken, StringComparison.Ordinal));
        Assert.All(transfer.Slots.Where(static slot => slot.CharacterToken.Length > 0), slot =>
        {
            Assert.StartsWith("char-", slot.CharacterToken, StringComparison.Ordinal);
            Assert.StartsWith("Shared-", slot.CharacterLabel, StringComparison.Ordinal);
            Assert.DoesNotContain("@", slot.CharacterLabel, StringComparison.Ordinal);
        });
        Assert.Equal(originalAccount, source.Slots[0].RequiredAccountKey);
        Assert.Equal(originalCharacter, source.Slots[0].RequiredCharacterKey);

        var imported = Assert.Single(service.Apply(envelope, [], []).PlannerGroups);
        Assert.All(imported.Slots, slot =>
        {
            Assert.True(slot.RequiredAccountKey.IsEmpty);
            Assert.True(slot.RequiredCharacterKey.IsEmpty);
            Assert.NotNull(slot.SharedIdentity);
        });
    }

    [Fact]
    public void ReexportOfImportedPlaceholdersDoesNotKrangleLabelsAgain()
    {
        var firstService = CreateService();
        Assert.True(firstService.TryExportPlan(BuildPlan(PlanA, "Plan"), KnownIdentities(), out var firstEncoded, out var error), error);
        Assert.True(firstService.TryDecode(firstEncoded, DadShareConstants.PlanKind, out var firstEnvelope, out error), error);
        var imported = Assert.Single(firstService.Apply(firstEnvelope!, [], []).PlannerGroups);
        var firstLabel = imported.Slots[0].SharedIdentity!.CharacterLabel;

        var secondService = new DadShareService(
            randomTokenFactory: () => Guid.NewGuid().ToString("N"),
            forceKrangle: _ => throw new InvalidOperationException("Shared labels must not be krangled twice."));
        Assert.True(secondService.TryExportPlan(imported, [], out var secondEncoded, out error), error);
        Assert.True(secondService.TryDecode(secondEncoded, DadShareConstants.PlanKind, out var secondEnvelope, out error), error);
        Assert.Equal(firstLabel, secondEnvelope!.Plan!.Slots[0].CharacterLabel);
    }

    [Fact]
    public void ImportedPlaceholderPersistsOnlyAsPlanConfigurationData()
    {
        var service = CreateService();
        Assert.True(service.TryExportPlan(BuildPlan(PlanA, "Plan"), KnownIdentities(), out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);
        var imported = Assert.Single(service.Apply(envelope!, [], []).PlannerGroups);

        var ipcJson = JsonSerializer.Serialize(imported, JsonOptions);
        var configJson = Newtonsoft.Json.JsonConvert.SerializeObject(imported);
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<DadPlannerGroup>(configJson);

        Assert.NotNull(restored);
        Assert.NotNull(restored.Slots[0].SharedIdentity);
        Assert.True(restored.Slots[0].RequiredAccountKey.IsEmpty);
        Assert.True(restored.Slots[0].RequiredCharacterKey.IsEmpty);
        Assert.DoesNotContain("sharedIdentity", ipcJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sharedStopTargetIdentityToken", ipcJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedIdentity", configJson, StringComparison.Ordinal);
        Assert.Contains("SharedStopTargetIdentityToken", configJson, StringComparison.Ordinal);
        Assert.DoesNotContain("account-real", configJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice Example@World", configJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalFinishFallbackIsMaterializedIntoTheShareVerbatim()
    {
        var service = CreateService();
        var plan = BuildPlan(PlanA, "Plan");
        plan.CompletionActions = null;
        var fallback = new DadCompletionActions
        {
            RunCommands = true,
            Commands = ["/echo exact global finish command"],
        };

        Assert.True(service.TryExportPlan(plan, [], fallback, out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);

        Assert.NotNull(envelope!.Plan!.CompletionActions);
        Assert.Equal(fallback.Commands, envelope.Plan.CompletionActions.Commands);
        Assert.Equal(fallback.Commands, Assert.Single(service.Apply(envelope, [], []).PlannerGroups).CompletionActions!.Commands);
    }

    [Fact]
    public void SameIdReplacementOverwritesShareableIdentityButPreservesExcludedLocalFields()
    {
        var service = CreateService();
        var transferSource = BuildPlan(PlanA, "Incoming");
        Assert.True(service.TryExportPlan(transferSource, KnownIdentities(), out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);

        var existing = BuildPlan(PlanA, "Local");
        existing.Slots[0].LaunchProfileId = "machine-profile";
        existing.Slots[0].CharacterLoadInstruction = new DadCharacterLoadInstruction
        {
            Enabled = true,
            CommandTemplate = "secret local command",
        };
        existing.ScheduleEnabled = true;
        existing.ScheduleCadenceHours = 12;
        var created = existing.CreatedAtUtc;

        var result = service.Apply(envelope!, [existing], []);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.ReplacedPlanCount);
        var replaced = Assert.Single(result.PlannerGroups);
        Assert.Equal("Incoming", replaced.DisplayName);
        Assert.True(replaced.Slots[0].RequiredAccountKey.IsEmpty);
        Assert.NotNull(replaced.Slots[0].SharedIdentity);
        Assert.Equal("machine-profile", replaced.Slots[0].LaunchProfileId);
        Assert.True(replaced.Slots[0].CharacterLoadInstruction.Enabled);
        Assert.Equal("secret local command", replaced.Slots[0].CharacterLoadInstruction.CommandTemplate);
        Assert.True(replaced.ScheduleEnabled);
        Assert.Equal(12, replaced.ScheduleCadenceHours);
        Assert.Equal(created, replaced.CreatedAtUtc);
    }

    [Fact]
    public void SameNameWithDifferentIdCoexistsAndPreviewUsesIdsOnly()
    {
        var service = CreateService();
        var incoming = BuildPlan(PlanA, "Same name");
        Assert.True(service.TryExportPlan(incoming, [], out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);
        var existing = BuildPlan(PlanC, "Same name");
        existing.InviteAuthority = DadInviteAuthority.NotNeeded;

        var preview = service.BuildImportPreview(envelope!, [existing], []);
        Assert.Empty(preview.ReplacementIds);
        var result = service.Apply(envelope!, [existing], []);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.AddedPlanCount);
        Assert.Equal(2, result.PlannerGroups.Count);
        Assert.Equal(2, result.PlannerGroups.Count(static plan => plan.DisplayName == "Same name"));
        Assert.Equal(DadInviteAuthority.NotNeeded, result.PlannerGroups.Single(static plan => plan.GroupId == PlanC).InviteAuthority);
    }

    [Fact]
    public void SchedulePreviewListsBundledPlanAndScheduleReplacementIds()
    {
        var service = CreateService();
        var plan = BuildPlan(PlanA, "Plan");
        var schedule = Schedule(ScheduleA, EntryA, PlanA);
        Assert.True(service.TryExportSchedule(schedule, [plan], [], out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out var envelope, out error), error);

        var preview = service.BuildImportPreview(envelope!, [BuildPlan(PlanA, "Old")], [Schedule(ScheduleA, EntryA, PlanA)]);
        Assert.Equal("Schedule", preview.Name);
        Assert.Equal(ScheduleA, preview.Id);
        Assert.Equal(1, preview.BundledPlanCount);
        Assert.Equal([PlanA, ScheduleA], preview.ReplacementIds);
    }

    [Fact]
    public void SharedCrewRemapClearsMarkersOnlyWhenRequiredLocalIdentityIsComplete()
    {
        var group = new DadPlannerGroup
        {
            SharedStopTargetIdentityToken = "character-token",
            StopPolicy = new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
            },
            Slots =
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    SharedIdentity = new DadSharedIdentityPlaceholder
                    {
                        IdentityToken = "account-only",
                        AccountToken = "account-one",
                    },
                },
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot2",
                    SharedIdentity = new DadSharedIdentityPlaceholder
                    {
                        IdentityToken = "character-token",
                        AccountToken = "account-two",
                        CharacterLabel = "Shared-abc",
                        RequiresCharacter = true,
                    },
                },
            ],
        };

        group.Slots[0].RequiredAccountKey = new DadAccountKey("local-one");
        DadSharedPlanRules.CompleteAccountOnlyRemap(group, group.Slots[0]);
        Assert.Null(group.Slots[0].SharedIdentity);

        group.Slots[1].RequiredAccountKey = new DadAccountKey("local-two");
        DadSharedPlanRules.CompleteCharacterRemap(group, group.Slots[1]);
        Assert.NotNull(group.Slots[1].SharedIdentity);
        group.Slots[1].RequiredCharacterKey = new DadCharacterKey("Local Character@World");
        DadSharedPlanRules.CompleteCharacterRemap(group, group.Slots[1]);
        Assert.Null(group.Slots[1].SharedIdentity);
        Assert.Equal("Local Character@World", group.StopPolicy.TargetCharacterKey.Value);
        Assert.Equal(string.Empty, group.SharedStopTargetIdentityToken);
        Assert.Empty(DadSharedPlanRules.BuildBlockers(group));
    }

    [Fact]
    public void ExplicitLocalStopChoiceClearsAnObsoleteSharedStopTarget()
    {
        var group = new DadPlannerGroup
        {
            SharedStopTargetIdentityToken = "shared-target",
            StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns },
        };
        DadSharedPlanRules.ReconcileStopTarget(group);
        Assert.Equal(string.Empty, group.SharedStopTargetIdentityToken);

        group.SharedStopTargetIdentityToken = "shared-target";
        group.StopPolicy.Mode = DadPlannerStopMode.TargetLevel;
        group.StopPolicy.TargetCharacterKey = new DadCharacterKey("Local@World");
        DadSharedPlanRules.ReconcileStopTarget(group);
        Assert.Equal(string.Empty, group.SharedStopTargetIdentityToken);
    }

    [Fact]
    public void MalformedWrongKindWrongVersionAndOversizePayloadsAreRejected()
    {
        var service = CreateService();
        Assert.False(service.TryDecode("not base64", DadShareConstants.PlanKind, out _, out var base64Error));
        Assert.Contains("Base64", base64Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.TryDecode(Convert.ToBase64String(Encoding.UTF8.GetBytes("not json")), DadShareConstants.PlanKind, out _, out var jsonError));
        Assert.Contains("JSON", jsonError, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.TryDecode(new string('A', DadShareConstants.MaxEncodedCharacters + 1), DadShareConstants.PlanKind, out _, out var sizeError));
        Assert.Contains("large", sizeError, StringComparison.OrdinalIgnoreCase);

        Assert.True(service.TryExportPlan(BuildPlan(PlanA, "Plan"), [], out var encoded, out var error), error);
        Assert.False(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out _, out var kindError));
        Assert.Contains("not a schedule", kindError, StringComparison.OrdinalIgnoreCase);
        var jsonWithUnknownMember = $"{{\"unknownMember\":true,{DecodeJson(encoded)[1..]}";
        Assert.False(service.TryDecode(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonWithUnknownMember)),
            DadShareConstants.PlanKind,
            out _,
            out var unknownMemberError));
        Assert.Contains("JSON", unknownMemberError, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);
        envelope!.Plan!.DutyDisplayName = null!;
        Assert.False(service.TryDecode(EncodeUnchecked(envelope), DadShareConstants.PlanKind, out _, out var nullError));
        Assert.Contains("null", nullError, StringComparison.OrdinalIgnoreCase);
        envelope.Plan.DutyDisplayName = string.Empty;
        envelope!.Schema++;
        Assert.False(service.TryDecode(EncodeUnchecked(envelope), DadShareConstants.PlanKind, out _, out var versionError));
        Assert.Contains("schema", versionError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndefinedEnumsDuplicateIdsBadRepeatsAndMissingReferencesAreRejectedAtomically()
    {
        var service = CreateService();
        var original = BuildPlan(PlanC, "Original");
        Assert.True(service.TryExportPlan(BuildPlan(PlanA, "Incoming"), [], out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var planEnvelope, out error), error);
        planEnvelope!.Plan!.ActivityMode = (DadPlannerActivityMode)999;
        Assert.False(service.TryDecode(EncodeUnchecked(planEnvelope), DadShareConstants.PlanKind, out _, out var enumError));
        Assert.Contains("enum", enumError, StringComparison.OrdinalIgnoreCase);
        var atomic = service.Apply(planEnvelope, [original], []);
        Assert.False(atomic.Success);
        Assert.Equal(PlanC, original.GroupId);
        Assert.Equal("Original", original.DisplayName);
        planEnvelope.Plan.ActivityMode = DadPlannerActivityMode.DailyRoulette;
        planEnvelope.Plan.GroupId = "invalid-id";
        Assert.False(service.TryDecode(EncodeUnchecked(planEnvelope), DadShareConstants.PlanKind, out _, out var idError));
        Assert.Contains("Plan ID", idError, StringComparison.OrdinalIgnoreCase);

        var schedule = Schedule(ScheduleA, EntryA, PlanA);
        Assert.True(service.TryExportSchedule(schedule, [BuildPlan(PlanA, "Plan")], [], out encoded, out error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out var scheduleEnvelope, out error), error);
        scheduleEnvelope!.Schedule!.Entries[0].RepeatCount = DadScheduleRules.MaxRepeatCount + 1;
        Assert.False(service.TryDecode(EncodeUnchecked(scheduleEnvelope), DadShareConstants.ScheduleKind, out _, out var repeatError));
        Assert.Contains("repeat", repeatError, StringComparison.OrdinalIgnoreCase);

        scheduleEnvelope.Schedule.Entries[0].RepeatCount = 1;
        scheduleEnvelope.Schedule.Entries.Add(scheduleEnvelope.Schedule.Entries[0]);
        Assert.False(service.TryDecode(EncodeUnchecked(scheduleEnvelope), DadShareConstants.ScheduleKind, out _, out var duplicateError));
        Assert.Contains("duplicated", duplicateError, StringComparison.OrdinalIgnoreCase);

        scheduleEnvelope.Schedule.Entries.RemoveAt(1);
        scheduleEnvelope.Plans.Add(scheduleEnvelope.Plans[0]);
        Assert.False(service.TryDecode(EncodeUnchecked(scheduleEnvelope), DadShareConstants.ScheduleKind, out _, out var duplicatePlanError));
        Assert.Contains("Bundled Plan ID", duplicatePlanError, StringComparison.OrdinalIgnoreCase);

        scheduleEnvelope.Plans.RemoveAt(1);
        scheduleEnvelope.Plans.Clear();
        Assert.False(service.TryDecode(EncodeUnchecked(scheduleEnvelope), DadShareConstants.ScheduleKind, out _, out var referenceError));
        Assert.Contains("unresolved", referenceError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportRejectsMissingSchedulePlanWithoutProducingClipboardText()
    {
        var service = CreateService();
        var schedule = Schedule(ScheduleA, EntryA, PlanA);
        Assert.False(service.TryExportSchedule(schedule, [BuildPlan(PlanB, "Other")], [], out var encoded, out var error));
        Assert.Equal(string.Empty, encoded);
        Assert.Contains("missing Plan", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false, false, "DAD")]
    [InlineData(false, true, false, "scheduler")]
    [InlineData(false, false, true, "Schedule")]
    public void MutationGuardBlocksEveryActiveWorkSource(bool dad, bool scheduler, bool schedule, string expected)
        => Assert.Contains(expected, DadShareService.GetMutationBlocker(dad, scheduler, schedule), StringComparison.OrdinalIgnoreCase);

    private static DadShareService CreateService()
    {
        var token = 0;
        return new DadShareService(
            randomTokenFactory: () => (++token).ToString("x32"),
            forceKrangle: value => $"Shared-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12]}");
    }

    private static DadPlannerGroup BuildPlan(string id, string name)
        => new()
        {
            GroupId = id,
            DisplayName = name,
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
            ConnectedOnly = true,
            SameDatacenterOnly = true,
            TransportOwner = DadTransportOwner.LanParty,
            QueueAuthority = DadQueueAuthority.Leader,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = 777,
            DutyDisplayName = "Alice Example duty",
            DutyExpectedPartySize = 4,
            RouletteTarget = new DadQueueTarget
            {
                Kind = DadQueueTargetKind.Roulette,
                RouletteId = 3,
                Key = "ContentRoulette:3",
                DisplayName = "Main Scenario Roulette",
            },
            MogtomePreset = "Primary Account route",
            MogtomeDutyPolicy = DadMogtomeDutyPolicies.PresetHandoff,
            RefreshTrustNpcLevels = true,
            StopPolicy = new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
                AfterRuns = 4,
                TargetLevel = 99,
                TargetCharacterKey = new DadCharacterKey("Alice Example@World"),
                TargetCharacterLabel = "Alice Example",
                SafetyCap = 20,
            },
            LevelingMode = new DadLevelingModeOptions
            {
                Enabled = true,
                GoalLevel = 90,
                JobOrder = DadLevelingJobOrder.HighestBelowGoal,
                DutyThresholds =
                [
                    new DadLevelingDutyThreshold
                    {
                        MinimumLevel = 50,
                        ContentFinderConditionId = 777,
                        DutyDisplayName = "Alice Example leveling duty",
                    },
                ],
            },
            CompletionActions = new DadCompletionActions
            {
                PlaySound = true,
                SoundEffectId = 7,
                RunCommands = true,
                Commands = ["/echo Alice Example", "/tell Primary Account exact command"],
                Utilities = new DadPostRunUtilities
                {
                    OpenGearCoffers = true,
                    GrandCompanyHandInCommand = "/ays gc Primary Account",
                },
            },
            Slots =
            [
                Slot("Slot1", "account-real", "Alice Example@World", 21, DadAllianceAssignment.A),
                Slot("Slot2", "account-real", "Bob Example@World", 24, DadAllianceAssignment.B),
                Slot("Slot3", "account-other", string.Empty, 32, DadAllianceAssignment.C),
            ],
            MapRunTemplate = "Alice Example map route",
            MapMode = DadMapCrewJobMode.GatherThenRun,
            ScheduleEnabled = true,
            ScheduleCadenceHours = 18,
            NextEligibleTimeUtc = DateTime.UtcNow.AddHours(1),
        };

    private static DadPlannerGroupSlot Slot(
        string id,
        string account,
        string character,
        uint job,
        DadAllianceAssignment allianceAssignment)
        => new()
        {
            SlotId = id,
            AllianceAssignment = allianceAssignment,
            RequiredAccountKey = new DadAccountKey(account),
            RequiredCharacterKey = new DadCharacterKey(character),
            RequiredJobId = job,
            AdsLootMode = DadAdsLootMode.Greed,
            LevelSeekTarget = 100,
            SkipIfDailyRouletteRewardReceived = id == "Slot1",
            WakePolicy = DadSchedulerWakePolicy.LoadCharacterIfOnline,
            LaunchProfileId = $"profile-{id}",
            CharacterLoadInstruction = new DadCharacterLoadInstruction
            {
                Enabled = true,
                CommandTemplate = $"load {character}",
            },
            AllowSubstitution = false,
        };

    private static DadScheduleEntry Entry(string entryId, string groupId, string name, int repeats)
        => new()
        {
            EntryId = entryId,
            GroupId = groupId,
            PresetName = name,
            RepeatCount = repeats,
        };

    private static DadScheduleDefinition Schedule(string scheduleId, string entryId, string planId)
        => new()
        {
            ScheduleId = scheduleId,
            DisplayName = "Schedule",
            Cadence = DadScheduleCadence.Manual,
            Entries = [Entry(entryId, planId, "Plan", 1)],
        };

    private static IReadOnlyList<DadShareKnownIdentity> KnownIdentities()
        =>
        [
            new DadShareKnownIdentity
            {
                AccountKey = "account-real",
                AccountAlias = "Primary Account",
                CharacterKey = "Alice Example@World",
                CharacterName = "Alice Example",
            },
            new DadShareKnownIdentity
            {
                AccountKey = "account-real",
                AccountAlias = "Primary Account",
                CharacterKey = "Bob Example@World",
                CharacterName = "Bob Example",
            },
            new DadShareKnownIdentity
            {
                AccountKey = "account-other",
                AccountAlias = "Other Account",
            },
        ];

    private static string DecodeJson(string encoded)
        => Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

    private static string EncodeUnchecked(DadShareEnvelopeDto envelope)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
}

public sealed class DadShareRenameRulesTests
{
    private const string OldPlan = "11111111111111111111111111111111";
    private const string NewPlan = "22222222222222222222222222222222";
    private const string OtherPlan = "33333333333333333333333333333333";
    private const string OldSchedule = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewSchedule = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherSchedule = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public void PlanRenameCascadesMutableReferencesAndLeavesHistoryUnchanged()
    {
        var service = new DadShareService();
        var plans = new List<DadPlannerGroup> { new() { GroupId = OldPlan }, new() { GroupId = OtherPlan } };
        var schedules = new List<DadScheduleDefinition>
        {
            new() { ScheduleId = OldSchedule, Entries = [new DadScheduleEntry { GroupId = OldPlan }] },
        };
        var jobs = new List<DadScheduledCrewJob> { new() { GroupId = OldPlan } };
        var options = new DadPresetPlannerOptions { SelectedPlannerGroupId = OldPlan };
        var history = new DadScheduledCrewJobResult { GroupId = OldPlan };

        var result = service.RenamePlanId(plans, schedules, jobs, options, OldPlan, NewPlan);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(NewPlan, plans[0].GroupId);
        Assert.Equal(NewPlan, schedules[0].Entries[0].GroupId);
        Assert.Equal(NewPlan, jobs[0].GroupId);
        Assert.Equal(NewPlan, options.SelectedPlannerGroupId);
        Assert.Equal(OldPlan, history.GroupId);
        Assert.Equal(4, result.UpdatedReferenceCount);
    }

    [Fact]
    public void ScheduleRenameCascadesMutableReferencesAndLeavesHistoryUnchanged()
    {
        var service = new DadShareService();
        var schedules = new List<DadScheduleDefinition>
        {
            new() { ScheduleId = OldSchedule },
            new() { ScheduleId = OtherSchedule },
        };
        var jobs = new List<DadScheduledCrewJob> { new() { ScheduleId = OldSchedule } };
        var active = new DadScheduleRunState { ScheduleId = OldSchedule };
        var history = new DadScheduleRunResult { ScheduleId = OldSchedule };

        var result = service.RenameScheduleId(schedules, jobs, active, OldSchedule, NewSchedule);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(NewSchedule, schedules[0].ScheduleId);
        Assert.Equal(NewSchedule, jobs[0].ScheduleId);
        Assert.Equal(NewSchedule, active.ScheduleId);
        Assert.Equal(OldSchedule, history.ScheduleId);
        Assert.Equal(3, result.UpdatedReferenceCount);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("22222222-2222-2222-2222-222222222222")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("00000000000000000000000000000000")]
    public void RenameRejectsNoncanonicalIds(string invalid)
    {
        var service = new DadShareService();
        var result = service.RenamePlanId(
            [new DadPlannerGroup { GroupId = OldPlan }],
            [],
            [],
            new DadPresetPlannerOptions(),
            OldPlan,
            invalid);
        Assert.False(result.Success);
    }

    [Fact]
    public void RenameRejectsDuplicateIdWithoutMutation()
    {
        var service = new DadShareService();
        var plans = new List<DadPlannerGroup>
        {
            new() { GroupId = OldPlan },
            new() { GroupId = NewPlan },
        };

        var result = service.RenamePlanId(plans, [], [], new DadPresetPlannerOptions(), OldPlan, NewPlan);
        Assert.False(result.Success);
        Assert.Equal(OldPlan, plans[0].GroupId);
    }

    [Fact]
    public void ScheduleRenameRejectsInvalidAndDuplicateIdsWithoutMutation()
    {
        var service = new DadShareService();
        var schedules = new List<DadScheduleDefinition>
        {
            new() { ScheduleId = OldSchedule },
            new() { ScheduleId = NewSchedule },
        };

        var invalid = service.RenameScheduleId(schedules, [], new DadScheduleRunState(), OldSchedule, "bad");
        var duplicate = service.RenameScheduleId(schedules, [], new DadScheduleRunState(), OldSchedule, NewSchedule);

        Assert.False(invalid.Success);
        Assert.False(duplicate.Success);
        Assert.Equal(OldSchedule, schedules[0].ScheduleId);
    }
}

public sealed class DadStarterShareBundleTests
{
    [Fact]
    public void EmptyInstallUsesStableManualBundleAndAnonymousFourSlotPlans()
    {
        var service = new DadShareService();
        Assert.True(DadStarterShareBundle.TryCreateEncoded(service, out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out var envelope, out error), error);

        var result = service.Apply(envelope!, [], [], DadShareApplyMode.SkipExisting);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.AddedPlanCount);
        Assert.True(result.ScheduleAdded);
        var schedule = Assert.Single(result.Schedules);
        Assert.Equal(DadStarterShareBundle.ScheduleId, schedule.ScheduleId);
        Assert.Equal(DadScheduleCadence.Manual, schedule.Cadence);
        Assert.Equal([DadStarterShareBundle.LevelingEntryId, DadStarterShareBundle.MainScenarioEntryId], schedule.Entries.Select(static entry => entry.EntryId));
        Assert.Equal([DadStarterShareBundle.LevelingPlanId, DadStarterShareBundle.MainScenarioPlanId], schedule.Entries.Select(static entry => entry.GroupId));
        Assert.All(schedule.Entries, static entry => Assert.Equal(1, entry.RepeatCount));

        var leveling = Assert.Single(result.PlannerGroups, static plan => plan.GroupId == DadStarterShareBundle.LevelingPlanId);
        var mainScenario = Assert.Single(result.PlannerGroups, static plan => plan.GroupId == DadStarterShareBundle.MainScenarioPlanId);
        Assert.Equal(1u, leveling.RouletteTarget.RouletteId);
        Assert.Equal(3u, mainScenario.RouletteTarget.RouletteId);
        Assert.All(result.PlannerGroups, plan =>
        {
            Assert.Equal(DadPlannerActivityMode.DailyRoulette, plan.ActivityMode);
            Assert.Equal(4, plan.Slots.Count);
            Assert.All(plan.Slots, slot => Assert.NotNull(slot.SharedIdentity));
        });
    }

    [Fact]
    public void PartialAndFullInstallNeverOverwriteKnownIds()
    {
        var service = new DadShareService();
        Assert.True(DadStarterShareBundle.TryCreateEncoded(service, out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out var envelope, out error), error);
        var existing = new DadPlannerGroup
        {
            GroupId = DadStarterShareBundle.LevelingPlanId,
            DisplayName = "Keep me",
        };

        var partial = service.Apply(envelope!, [existing], [], DadShareApplyMode.SkipExisting);
        Assert.True(partial.Success, partial.Summary);
        Assert.Equal("Keep me", partial.PlannerGroups.Single(plan => plan.GroupId == DadStarterShareBundle.LevelingPlanId).DisplayName);
        Assert.Equal(1, partial.AddedPlanCount);
        Assert.Equal(1, partial.SkippedPlanCount);
        Assert.True(partial.ScheduleAdded);

        var full = service.Apply(envelope!, partial.PlannerGroups, partial.Schedules, DadShareApplyMode.SkipExisting);
        Assert.True(full.Success, full.Summary);
        Assert.Equal(0, full.AddedPlanCount);
        Assert.Equal(2, full.SkippedPlanCount);
        Assert.True(full.ScheduleSkipped);
        Assert.False(full.ScheduleReplaced);
        Assert.Equal("Keep me", full.PlannerGroups.Single(plan => plan.GroupId == DadStarterShareBundle.LevelingPlanId).DisplayName);
    }

    [Fact]
    public void ExistingStarterScheduleDoesNotPreventMissingPlansFromInstalling()
    {
        var service = new DadShareService();
        Assert.True(DadStarterShareBundle.TryCreateEncoded(service, out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.ScheduleKind, out var envelope, out error), error);
        var existingSchedule = new DadScheduleDefinition
        {
            ScheduleId = DadStarterShareBundle.ScheduleId,
            DisplayName = "Keep custom schedule",
            Cadence = DadScheduleCadence.DailyReset,
        };

        var result = service.Apply(envelope!, [], [existingSchedule], DadShareApplyMode.SkipExisting);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.AddedPlanCount);
        Assert.True(result.ScheduleSkipped);
        Assert.Equal("Keep custom schedule", Assert.Single(result.Schedules).DisplayName);
        Assert.Equal(DadScheduleCadence.DailyReset, result.Schedules[0].Cadence);
    }
}

public sealed class DadP1181ActivityCompatibilityTests
{
    [Fact]
    public void FreshPlannerDraftsDefaultToDutySupportAndMsqIsNotACreationActivity()
    {
        var options = new DadPresetPlannerOptions();
        var group = new DadPlannerGroup();

        Assert.Equal(DadPlannerActivityMode.DutySupport, options.ActivityMode);
        Assert.Equal(DadPlannerRunFamily.LevelingNpc, options.RunFamily);
        Assert.Equal(DadTransportOwner.DadDirect, options.TransportOwner);
        Assert.Equal(DadQueueAuthority.LocalOnly, options.QueueAuthority);
        Assert.Equal(1, options.DutyExpectedPartySize);
        Assert.Equal(DadPlannerActivityMode.DutySupport, group.ActivityMode);
        Assert.Equal(DadTransportOwner.DadDirect, group.TransportOwner);
        Assert.Equal(DadQueueAuthority.LocalOnly, group.QueueAuthority);
        Assert.Equal(1, group.DutyExpectedPartySize);
        Assert.False(DadLegacyActivityRules.IsCreationActivity(DadPlannerActivityMode.Msq));
        Assert.All(
            Enum.GetValues<DadPlannerActivityMode>().Where(DadLegacyActivityRules.IsCreationActivity),
            static activity => Assert.NotEqual(DadPlannerActivityMode.Msq, activity));
    }

    [Fact]
    public void LegacyMsqNumericValueDeserializesUnchangedButIsBlocked()
    {
        var options = JsonSerializer.Deserialize<DadPresetPlannerOptions>("{\"activityMode\":0}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(options);
        Assert.Equal(DadPlannerActivityMode.Msq, options.ActivityMode);
        Assert.Equal(0, (int)DadPlannerActivityMode.Msq);
        Assert.Contains("unsupported", DadLegacyActivityRules.GetValidationBlocker(options.ActivityMode), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainScenarioRouletteRemainsASeparateSupportedTarget()
    {
        var target = new DadQueueTarget
        {
            Kind = DadQueueTargetKind.Roulette,
            RouletteId = DadRouletteCatalogProjection.MainScenarioRouletteId,
            Key = "ContentRoulette:3",
            DisplayName = "Main Scenario",
        };
        var task = DadDailyRoulettePlannerRules.BuildWireCompatibleTask(target);

        Assert.Equal(DadPlannerActivityMode.DailyRoulette, new DadPlannerGroup
        {
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
        }.ActivityMode);
        Assert.Equal(3u, task.QueueTarget.RouletteId);
        Assert.Equal("ContentRoulette:3", task.QueueTarget.Key);
        Assert.Empty(DadLegacyActivityRules.GetValidationBlocker(DadPlannerActivityMode.DailyRoulette));
    }
}
