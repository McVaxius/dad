using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoPartyFleetMatrixService
{
    private readonly Configuration configuration;
    private readonly Func<string> mutationBlocker;
    private readonly Action saveConfiguration;
    private readonly object transactionGate = new();

    public DadAutoPartyFleetMatrixService(
        Configuration configuration,
        Func<string> mutationBlocker,
        Action? saveConfiguration = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.mutationBlocker = mutationBlocker ?? throw new ArgumentNullException(nameof(mutationBlocker));
        this.saveConfiguration = saveConfiguration ?? configuration.Save;
        configuration.AutoPartyFleet ??= new DadAutoPartyFleetConfiguration();
        configuration.AutoPartyFleet.Normalize();
    }

    public DadAutoPartyFleetPreview BuildPreview(DateTime? nowUtc = null)
    {
        lock (transactionGate)
        {
            var matrix = configuration.AutoPartyFleet.Clone().Normalize();
            var issues = Validate(matrix);
            if (issues.Count > 0)
                return new(matrix.Revision, BuildFingerprint(matrix), [], [], issues);

            var timestamp = EnsureUtc(nowUtc ?? DateTime.UtcNow);
            var rows = matrix.Rows.ToDictionary(static row => row.RowId, StringComparer.OrdinalIgnoreCase);
            var crews = matrix.CrewSets.ToDictionary(static crew => crew.CrewSetId, StringComparer.OrdinalIgnoreCase);
            var plans = new List<DadPlannerGroup>();
            var schedules = new List<DadScheduleDefinition>();
            foreach (var blueprint in matrix.Blueprints.OrderBy(static blueprint => blueprint.BlueprintId, StringComparer.OrdinalIgnoreCase))
            {
                var blueprintPlans = new List<DadPlannerGroup>();
                foreach (var crewId in blueprint.CrewSetIds)
                {
                    var crew = crews[crewId];
                    var plan = BuildPlan(
                        blueprint,
                        crew,
                        rows,
                        configuration.AutoParty.RemoteBindings,
                        timestamp);
                    plans.Add(plan);
                    blueprintPlans.Add(plan);
                }

                if (blueprint.CreateSchedule)
                    schedules.Add(BuildSchedule(blueprint, blueprintPlans, timestamp));
            }

            return new(
                matrix.Revision,
                BuildFingerprint(matrix),
                plans,
                schedules,
                []);
        }
    }

    public DadAutoPartyFleetMutationResult Apply(DateTime? nowUtc = null)
    {
        lock (transactionGate)
        {
            if (!configuration.AutoPartyFleet.Enabled)
                return Failure("dad-fleet-disabled", "Enable Fleet/Crew Matrix before applying generated Plans.");
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-fleet-mutation-locked", blocker);

            var preview = BuildPreview(nowUtc);
            if (!preview.CanApply)
                return Failure("dad-fleet-preview-invalid", preview.Summary);

            var managedGroupIds = configuration.AutoPartyFleet.ManagedPlannerGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var managedScheduleIds = configuration.AutoPartyFleet.ManagedScheduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var generatedGroupIds = preview.PlannerGroups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var generatedScheduleIds = preview.Schedules.Select(static schedule => schedule.ScheduleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (configuration.PlannerGroups.Any(group => generatedGroupIds.Contains(group.GroupId) && !managedGroupIds.Contains(group.GroupId)) ||
                configuration.Schedules.Any(schedule => generatedScheduleIds.Contains(schedule.ScheduleId) && !managedScheduleIds.Contains(schedule.ScheduleId)))
                return Failure("dad-fleet-unowned-id-collision", "A deterministic generated ID collides with an unowned Plan or Schedule.");

            var previousGroups = ClonePlannerGroups(configuration.PlannerGroups);
            var previousSchedules = CloneSchedules(configuration.Schedules);
            var previousMatrix = configuration.AutoPartyFleet.Clone();
            var nextGroups = configuration.PlannerGroups
                .Where(group => !managedGroupIds.Contains(group.GroupId))
                .Select(ClonePlannerGroup)
                .Concat(preview.PlannerGroups.Select(ClonePlannerGroup))
                .ToList();
            var nextSchedules = configuration.Schedules
                .Where(schedule => !managedScheduleIds.Contains(schedule.ScheduleId))
                .Select(static schedule => schedule.Clone())
                .Concat(preview.Schedules.Select(static schedule => schedule.Clone()))
                .ToList();
            var token = Guid.NewGuid().ToString("N");

            try
            {
                configuration.PlannerGroups = nextGroups;
                configuration.Schedules = nextSchedules;
                configuration.AutoPartyFleet.Revision++;
                configuration.AutoPartyFleet.ManagedPlannerGroupIds = generatedGroupIds.Order(StringComparer.OrdinalIgnoreCase).ToList();
                configuration.AutoPartyFleet.ManagedScheduleIds = generatedScheduleIds.Order(StringComparer.OrdinalIgnoreCase).ToList();
                configuration.AutoPartyFleet.UndoSnapshot = new DadAutoPartyFleetUndoSnapshot
                {
                    UndoToken = token,
                    AppliedRevision = configuration.AutoPartyFleet.Revision,
                    AppliedStateFingerprint = BuildAppliedStateFingerprint(nextGroups, nextSchedules),
                    CapturedAtUtc = EnsureUtc(nowUtc ?? DateTime.UtcNow),
                    PlannerGroups = previousGroups,
                    Schedules = previousSchedules,
                };
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.PlannerGroups = previousGroups;
                configuration.Schedules = previousSchedules;
                configuration.AutoPartyFleet = previousMatrix;
                return Failure("dad-fleet-save-failed", $"Fleet apply was rolled back: {exception.GetType().Name}.");
            }

            return new(
                true,
                "dad-fleet-applied",
                $"Applied {preview.PlannerGroups.Count} Plan(s) and {preview.Schedules.Count} Schedule(s) as one Matrix revision.",
                token,
                preview.Fingerprint,
                preview.PlannerGroups.Count,
                preview.Schedules.Count);
        }
    }

    public DadAutoPartyFleetMutationResult Undo(string? undoToken = null)
    {
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-fleet-mutation-locked", blocker);
            var snapshot = configuration.AutoPartyFleet.UndoSnapshot;
            if (snapshot == null)
                return Failure("dad-fleet-undo-unavailable", "No Fleet apply is available to undo.");
            if (!string.IsNullOrWhiteSpace(undoToken) && !string.Equals(snapshot.UndoToken, undoToken.Trim(), StringComparison.Ordinal))
                return Failure("dad-fleet-undo-token-mismatch", "The Fleet undo token does not match the current revision.");
            if (string.IsNullOrWhiteSpace(snapshot.AppliedStateFingerprint) ||
                !string.Equals(
                    snapshot.AppliedStateFingerprint,
                    BuildAppliedStateFingerprint(configuration.PlannerGroups, configuration.Schedules),
                    StringComparison.Ordinal))
                return Failure("dad-fleet-undo-drift", "Plans or Schedules changed after Fleet apply; undo will not overwrite later work.");

            var currentGroups = ClonePlannerGroups(configuration.PlannerGroups);
            var currentSchedules = CloneSchedules(configuration.Schedules);
            var currentMatrix = configuration.AutoPartyFleet.Clone();
            try
            {
                configuration.PlannerGroups = ClonePlannerGroups(snapshot.PlannerGroups);
                configuration.Schedules = CloneSchedules(snapshot.Schedules);
                configuration.AutoPartyFleet.Revision++;
                configuration.AutoPartyFleet.ManagedPlannerGroupIds = [];
                configuration.AutoPartyFleet.ManagedScheduleIds = [];
                configuration.AutoPartyFleet.UndoSnapshot = null;
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.PlannerGroups = currentGroups;
                configuration.Schedules = currentSchedules;
                configuration.AutoPartyFleet = currentMatrix;
                return Failure("dad-fleet-undo-save-failed", $"Fleet undo was rolled back: {exception.GetType().Name}.");
            }

            return new(true, "dad-fleet-undone", "Restored the exact pre-apply Plan and Schedule collections.");
        }
    }

    public DadAutoPartyFleetImportResult ImportTsv(string? tsv)
    {
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return new(false, "dad-fleet-mutation-locked", blocker);
            var parsed = DadAutoPartyFleetTsv.Parse(tsv);
            if (!parsed.Succeeded || parsed.Draft == null)
                return parsed;

            var previous = configuration.AutoPartyFleet.Clone();
            try
            {
                var existingRows = configuration.AutoPartyFleet.Rows.ToDictionary(static row => row.RowId, StringComparer.OrdinalIgnoreCase);
                configuration.AutoPartyFleet.Rows = parsed.Draft.Rows.Select(row =>
                {
                    var imported = row.Clone();
                    if (!imported.IsRemote && existingRows.TryGetValue(imported.RowId, out var existing) && !existing.IsRemote)
                    {
                        imported.AccountKey = existing.AccountKey;
                        imported.CharacterKey = existing.CharacterKey;
                    }
                    return imported;
                }).ToList();
                configuration.AutoPartyFleet.CrewSets = parsed.Draft.CrewSets.Select(static crew => crew.Clone()).ToList();
                configuration.AutoPartyFleet.Revision++;
                configuration.AutoPartyFleet.Normalize();
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.AutoPartyFleet = previous;
                return new(false, "dad-fleet-import-save-failed", $"Fleet TSV import was rolled back: {exception.GetType().Name}.");
            }
            return new(true, "dad-fleet-imported", $"Imported {parsed.Draft.Rows.Count} Fleet row(s) and {parsed.Draft.CrewSets.Count} Crew Set(s).", parsed.Draft);
        }
    }

    public DadAutoPartyFleetMutationResult MergeLocalRoster(IEnumerable<DadAcquiredCharacter>? characters)
    {
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-fleet-mutation-locked", blocker);
            var candidates = (characters ?? [])
                .Where(static character => character != null &&
                    !string.IsNullOrWhiteSpace(character.CharacterKey) &&
                    (!string.IsNullOrWhiteSpace(character.AccountId) || !string.IsNullOrWhiteSpace(character.AccountAlias)) &&
                    character.CurrentJobId > 0)
                .GroupBy(static character => character.CharacterKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 0)
                return Failure("dad-fleet-roster-empty", "The current DAD roster has no bound characters with a selected job.");

            var previous = configuration.AutoPartyFleet.Clone();
            var existingByCharacter = configuration.AutoPartyFleet.Rows
                .Where(static row => !row.IsRemote && !string.IsNullOrWhiteSpace(row.CharacterKey))
                .ToDictionary(static row => row.CharacterKey, StringComparer.OrdinalIgnoreCase);
            var added = 0;
            var updated = 0;
            try
            {
                foreach (var character in candidates)
                {
                    if (existingByCharacter.TryGetValue(character.CharacterKey, out var existing))
                    {
                        existing.AccountKey = string.IsNullOrWhiteSpace(character.AccountId) ? character.AccountAlias : character.AccountId;
                        existing.JobId = character.CurrentJobId!.Value;
                        existing.Enabled = true;
                        updated++;
                        continue;
                    }
                    if (configuration.AutoPartyFleet.Rows.Count >= DadAutoPartyFleetLimits.MaxFleetRows)
                        break;
                    var row = new DadAutoPartyFleetRow
                    {
                        RowId = "row-" + Guid.NewGuid().ToString("N"),
                        OpaqueCharacterId = "opaque-" + Guid.NewGuid().ToString("N"),
                        AccountKey = string.IsNullOrWhiteSpace(character.AccountId) ? character.AccountAlias : character.AccountId,
                        CharacterKey = character.CharacterKey,
                        Role = DadPartyRole.Any,
                        JobId = character.CurrentJobId!.Value,
                        IsRemote = false,
                        Enabled = true,
                    };
                    configuration.AutoPartyFleet.Rows.Add(row);
                    existingByCharacter.Add(row.CharacterKey, row);
                    added++;
                }
                configuration.AutoPartyFleet.Revision++;
                configuration.AutoPartyFleet.Normalize();
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.AutoPartyFleet = previous;
                return Failure("dad-fleet-roster-merge-save-failed", $"Fleet roster merge was rolled back: {exception.GetType().Name}.");
            }
            return new(true, "dad-fleet-roster-merged", $"Merged the local roster: {added} added, {updated} refreshed.");
        }
    }

    public string ExportTsv()
    {
        lock (transactionGate)
            return DadAutoPartyFleetTsv.Export(configuration.AutoPartyFleet);
    }

    public DadAutoPartyFleetMutationResult SetEnabled(bool enabled)
    {
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-fleet-mutation-locked", blocker);
            if (configuration.AutoPartyFleet.Enabled == enabled)
                return new(true, "dad-fleet-enabled-unchanged", "Fleet/Crew Matrix enablement is unchanged.");
            var previous = configuration.AutoPartyFleet.Clone();
            try
            {
                configuration.AutoPartyFleet.Enabled = enabled;
                configuration.AutoPartyFleet.Revision++;
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.AutoPartyFleet = previous;
                return Failure("dad-fleet-enable-save-failed", $"Fleet enablement was rolled back: {exception.GetType().Name}.");
            }
            return new(true, enabled ? "dad-fleet-enabled" : "dad-fleet-disabled", $"Fleet/Crew Matrix apply is now {(enabled ? "enabled" : "disabled")}.");
        }
    }

    public DadAutoPartyFleetMutationResult AddBlueprint(DadAutoPartyFleetBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-fleet-mutation-locked", blocker);
            if (configuration.AutoPartyFleet.Blueprints.Count >= DadAutoPartyFleetLimits.MaxBlueprints)
                return Failure("dad-fleet-blueprint-limit", "The Fleet blueprint limit has been reached.");
            var normalized = blueprint.Clone().Normalize();
            if (string.IsNullOrWhiteSpace(normalized.BlueprintId) ||
                configuration.AutoPartyFleet.Blueprints.Any(candidate => string.Equals(candidate.BlueprintId, normalized.BlueprintId, StringComparison.OrdinalIgnoreCase)))
                return Failure("dad-fleet-blueprint-id-invalid", "The Fleet blueprint ID is empty or already exists.");

            var previous = configuration.AutoPartyFleet.Clone();
            try
            {
                configuration.AutoPartyFleet.Blueprints.Add(normalized);
                configuration.AutoPartyFleet.Revision++;
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.AutoPartyFleet = previous;
                return Failure("dad-fleet-blueprint-save-failed", $"Fleet blueprint creation was rolled back: {exception.GetType().Name}.");
            }
            return new(true, "dad-fleet-blueprint-added", $"Added Fleet blueprint '{normalized.DisplayName}'.");
        }
    }

    public DadAutoPartyFleetMutationResult RemoveBlueprint(string blueprintId)
    {
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-fleet-mutation-locked", blocker);
            var normalizedId = DadAutoPartyFleetConfiguration.NormalizeIdentifier(blueprintId);
            var existing = configuration.AutoPartyFleet.Blueprints.FirstOrDefault(candidate =>
                string.Equals(candidate.BlueprintId, normalizedId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                return Failure("dad-fleet-blueprint-missing", "The Fleet blueprint no longer exists.");

            var previous = configuration.AutoPartyFleet.Clone();
            try
            {
                configuration.AutoPartyFleet.Blueprints.Remove(existing);
                configuration.AutoPartyFleet.Revision++;
                saveConfiguration();
            }
            catch (Exception exception)
            {
                configuration.AutoPartyFleet = previous;
                return Failure("dad-fleet-blueprint-delete-save-failed", $"Fleet blueprint deletion was rolled back: {exception.GetType().Name}.");
            }
            return new(true, "dad-fleet-blueprint-removed", $"Removed Fleet blueprint '{existing.DisplayName}'.");
        }
    }

    private static List<DadAutoPartyFleetIssue> Validate(DadAutoPartyFleetConfiguration matrix)
    {
        var issues = new List<DadAutoPartyFleetIssue>();
        ValidateUniqueIdentifiers(matrix.Rows.Select(static row => row.RowId), "row", issues);
        ValidateUniqueIdentifiers(matrix.CrewSets.Select(static crew => crew.CrewSetId), "crew", issues);
        ValidateUniqueIdentifiers(matrix.Blueprints.Select(static blueprint => blueprint.BlueprintId), "blueprint", issues);

        var rowIds = matrix.Rows.Select(static row => row.RowId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var characterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opaqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in matrix.Rows.Where(static row => row.Enabled))
        {
            if (string.IsNullOrWhiteSpace(row.RowId) || row.JobId == 0)
                issues.Add(new("dad-fleet-row-invalid", "Every enabled Fleet row requires a row ID and strict requested job ID."));
            if (row.IsRemote)
            {
                if (string.IsNullOrWhiteSpace(row.OpaqueCharacterId) || !opaqueIds.Add(row.OpaqueCharacterId))
                    issues.Add(new("dad-fleet-remote-identity-invalid", "Every enabled remote row requires a unique opaque character ID."));
            }
            else if (string.IsNullOrWhiteSpace(row.AccountKey) || string.IsNullOrWhiteSpace(row.CharacterKey) || !characterKeys.Add(row.CharacterKey))
            {
                issues.Add(new("dad-fleet-local-identity-invalid", "Every enabled local row requires account and unique character keys."));
            }
        }

        var assignedRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var crew in matrix.CrewSets)
        {
            if (string.IsNullOrWhiteSpace(crew.CrewSetId) || string.IsNullOrWhiteSpace(crew.DisplayName) || crew.FleetRowIds.Count is < 1 or > DadAutoPartyFleetLimits.MaxCrewMembers)
                issues.Add(new("dad-fleet-crew-invalid", "Each Crew Set requires an ID, name, and one to eight ordered members."));
            foreach (var rowId in crew.FleetRowIds)
            {
                if (!rowIds.Contains(rowId))
                    issues.Add(new("dad-fleet-crew-row-missing", $"Crew Set '{crew.CrewSetId}' references a missing Fleet row."));
                else if (!assignedRows.Add(rowId))
                    issues.Add(new("dad-fleet-row-multiple-crews", $"Fleet row '{rowId}' belongs to more than one Crew Set."));
                else if (!matrix.Rows.First(row => string.Equals(row.RowId, rowId, StringComparison.OrdinalIgnoreCase)).Enabled)
                    issues.Add(new("dad-fleet-crew-row-disabled", $"Crew Set '{crew.CrewSetId}' references a disabled Fleet row."));
            }
        }

        var crewIds = matrix.CrewSets.Select(static crew => crew.CrewSetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedParties = 0;
        foreach (var blueprint in matrix.Blueprints)
        {
            if (string.IsNullOrWhiteSpace(blueprint.BlueprintId) || string.IsNullOrWhiteSpace(blueprint.DisplayName) || blueprint.DutyContentFinderConditionId == 0)
                issues.Add(new("dad-fleet-blueprint-invalid", "Each blueprint requires an ID, name, and exact Content Finder condition ID."));
            if (blueprint.CrewSetIds.Count == 0)
                issues.Add(new("dad-fleet-blueprint-empty", $"Blueprint '{blueprint.BlueprintId}' has no Crew Sets."));
            foreach (var crewId in blueprint.CrewSetIds)
            {
                if (!crewIds.Contains(crewId))
                    issues.Add(new("dad-fleet-blueprint-crew-missing", $"Blueprint '{blueprint.BlueprintId}' references a missing Crew Set."));
            }
            generatedParties += blueprint.CrewSetIds.Count;
        }
        if (generatedParties > DadAutoPartyFleetLimits.MaxGeneratedParties)
            issues.Add(new("dad-fleet-party-limit", $"A Matrix revision can generate at most {DadAutoPartyFleetLimits.MaxGeneratedParties} parties."));
        return issues.Distinct().ToList();
    }

    private static void ValidateUniqueIdentifiers(IEnumerable<string> values, string kind, ICollection<DadAutoPartyFleetIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                issues.Add(new($"dad-fleet-{kind}-id-invalid", $"Fleet {kind} IDs must be non-empty and unique."));
        }
    }

    private static DadPlannerGroup BuildPlan(
        DadAutoPartyFleetBlueprint blueprint,
        DadAutoPartyCrewSet crew,
        IReadOnlyDictionary<string, DadAutoPartyFleetRow> rows,
        IReadOnlyList<DadAutoPartyRemoteBinding> remoteBindings,
        DateTime timestamp)
    {
        var slots = crew.FleetRowIds.Select((rowId, index) =>
        {
            var row = rows[rowId];
            return new DadPlannerGroupSlot
            {
                SlotId = DadPlannerSlotRules.FormatSlotId(index + 1),
                AllianceAssignment = row.AllianceAssignment,
                RequiredRole = row.Role,
                RequiredAccountKey = new DadAccountKey(row.IsRemote ? string.Empty : row.AccountKey),
                RequiredCharacterKey = new DadCharacterKey(row.IsRemote ? string.Empty : row.CharacterKey),
                RequiredJobId = row.JobId,
                WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly,
                CharacterLoadInstruction = new DadCharacterLoadInstruction { Enabled = false, DryRun = true },
                SharedIdentity = row.IsRemote
                    ? new DadSharedIdentityPlaceholder
                    {
                        IdentityToken = row.OpaqueCharacterId,
                        CharacterLabel = $"Remote slot {index + 1}",
                        RequiresCharacter = true,
                    }
                    : null,
                AllowSubstitution = false,
            };
        }).ToList();
        var remoteRowIds = crew.FleetRowIds
            .Where(rowId => rows[rowId].IsRemote)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedJobsByIdentity = remoteBindings
            .Where(static binding => binding.IsValid)
            .GroupBy(static binding => binding.OpaqueCharacterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static binding => binding.RequestedJobId)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var formationOnly = remoteRowIds.Count > 0 && remoteRowIds.All(rowId =>
            requestedJobsByIdentity.TryGetValue(rows[rowId].OpaqueCharacterId, out var requestedJobs) &&
            requestedJobs.Count == 1 &&
            string.Equals(
                requestedJobs[0],
                rows[rowId].JobId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
        return new DadPlannerGroup
        {
            GroupId = StableId("fleet-plan-v1", blueprint.BlueprintId, crew.CrewSetId),
            DisplayName = LimitText($"{blueprint.DisplayName} - {crew.DisplayName}"),
            RunFamily = blueprint.RunFamily,
            ActivityMode = blueprint.ActivityMode,
            OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
            ConnectedOnly = true,
            SameDatacenterOnly = true,
            AllowStaleForPlanning = false,
            TransportOwner = DadTransportOwner.DadDirect,
            QueueAuthority = DadQueueAuthority.LocalOnly,
            InviteAuthority = DadInviteAuthority.PresetLeader,
            DutyContentFinderConditionId = blueprint.DutyContentFinderConditionId,
            DutyDisplayName = blueprint.DutyDisplayName,
            DutyUnsynced = blueprint.DutyUnsynced,
            DutyExpectedPartySize = slots.Count,
            Slots = slots,
            AutoPartyProposalId = string.Empty,
            AutoPartyFormationOnly = formationOnly,
            IsTemplate = false,
            ScheduleEnabled = false,
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        };
    }

    private static DadScheduleDefinition BuildSchedule(
        DadAutoPartyFleetBlueprint blueprint,
        IReadOnlyList<DadPlannerGroup> plans,
        DateTime timestamp)
    {
        var scheduleId = StableId("fleet-schedule-v1", blueprint.BlueprintId);
        return new DadScheduleDefinition
        {
            SchemaVersion = 1,
            Revision = 1,
            ScheduleId = scheduleId,
            DisplayName = LimitText($"{blueprint.DisplayName} Fleet"),
            Cadence = blueprint.ScheduleCadence,
            Entries = plans.Select(plan => new DadScheduleEntry
            {
                EntryId = StableId("fleet-entry-v1", scheduleId, plan.GroupId),
                GroupId = plan.GroupId,
                PresetName = plan.DisplayName,
                RepeatCount = blueprint.RepeatCount,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
            }).ToList(),
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        }.Normalize();
    }

    private static string BuildFingerprint(DadAutoPartyFleetConfiguration matrix)
    {
        var builder = new StringBuilder();
        Append(builder, matrix.Revision.ToString(CultureInfo.InvariantCulture));
        foreach (var row in matrix.Rows.OrderBy(static row => row.RowId, StringComparer.OrdinalIgnoreCase))
        {
            Append(builder, row.RowId, row.OpaqueCharacterId, row.AccountKey, row.CharacterKey, row.AllianceAssignment.ToString(), row.Role.ToString(),
                row.JobId.ToString(CultureInfo.InvariantCulture), row.IsRemote.ToString(), row.Enabled.ToString());
        }
        foreach (var crew in matrix.CrewSets.OrderBy(static crew => crew.CrewSetId, StringComparer.OrdinalIgnoreCase))
            Append(builder, [crew.CrewSetId, crew.DisplayName, .. crew.FleetRowIds]);
        foreach (var blueprint in matrix.Blueprints.OrderBy(static blueprint => blueprint.BlueprintId, StringComparer.OrdinalIgnoreCase))
        {
            Append(builder, blueprint.BlueprintId, blueprint.DisplayName, blueprint.RunFamily.ToString(), blueprint.ActivityMode.ToString(),
                blueprint.DutyContentFinderConditionId.ToString(CultureInfo.InvariantCulture), blueprint.DutyDisplayName,
                blueprint.DutyUnsynced.ToString(), blueprint.CreateSchedule.ToString(), blueprint.ScheduleCadence.ToString(),
                blueprint.RepeatCount.ToString(CultureInfo.InvariantCulture));
            Append(builder, blueprint.CrewSetIds.ToArray());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string BuildAppliedStateFingerprint(
        IEnumerable<DadPlannerGroup> groups,
        IEnumerable<DadScheduleDefinition> schedules)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            PlannerGroups = groups,
            Schedules = schedules,
        });
        try
        {
            return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void Append(StringBuilder builder, params string[] values)
    {
        foreach (var value in values)
            builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static string StableId(params string[] components)
    {
        var builder = new StringBuilder();
        Append(builder, components);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32].ToLowerInvariant();
    }

    private static string LimitText(string value)
        => value[..Math.Min(value.Length, DadAutoPartyFleetLimits.MaxTextLength)];

    private static DadPlannerGroup ClonePlannerGroup(DadPlannerGroup group)
        => DadSchedulerGroupCloneRules.CloneWithSlots(group, group.Slots);

    private static List<DadPlannerGroup> ClonePlannerGroups(IEnumerable<DadPlannerGroup>? groups)
        => (groups ?? []).Select(ClonePlannerGroup).ToList();

    private static List<DadScheduleDefinition> CloneSchedules(IEnumerable<DadScheduleDefinition>? schedules)
        => (schedules ?? []).Select(static schedule => schedule.Clone()).ToList();

    private string GetMutationBlocker()
    {
        try
        {
            return mutationBlocker()?.Trim() ?? string.Empty;
        }
        catch (Exception exception)
        {
            return $"Fleet mutation readiness could not be verified ({exception.GetType().Name}).";
        }
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static DadAutoPartyFleetMutationResult Failure(string safeCode, string summary)
        => new(false, safeCode, summary);
}
