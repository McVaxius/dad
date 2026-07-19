using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dad.Models;

namespace dad.Services;

public sealed class DadPresetBatchWizardService
{
    private static readonly Regex IndexFormatPattern = new(
        @"\{Index:(?<zeros>0+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Configuration configuration;
    private readonly Func<string> mutationBlocker;
    private readonly Action saveConfiguration;
    private readonly object transactionGate = new();
    private UndoSnapshot? undoSnapshot;

    public DadPresetBatchWizardService(
        Configuration configuration,
        Func<string> mutationBlocker,
        Action? saveConfiguration = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.mutationBlocker = mutationBlocker ?? throw new ArgumentNullException(nameof(mutationBlocker));
        this.saveConfiguration = saveConfiguration ?? configuration.Save;
    }

    public bool CanUndo
    {
        get
        {
            lock (transactionGate)
                return undoSnapshot != null;
        }
    }

    public string UndoToken
    {
        get
        {
            lock (transactionGate)
                return undoSnapshot?.Token ?? string.Empty;
        }
    }

    public DadPresetBatchPreview BuildPreview(
        DadPresetBatchDraft? draft,
        DadAccountRosterCatalog? catalog,
        DateTime? nowUtc = null)
    {
        lock (transactionGate)
        {
            var currentPlannerGroups = configuration.PlannerGroups ?? [];
            var currentSchedules = configuration.Schedules ?? [];
            var sourceConfigurationFingerprint = BuildConfigurationFingerprint(
                currentPlannerGroups,
                currentSchedules);
            var working = draft?.Clone() ?? new DadPresetBatchDraft();
            var roster = catalog?.Clone() ?? new DadAccountRosterCatalog();
            var issues = new List<DadPresetBatchIssue>();

            ValidateDraftShape(working, issues);
            var pools = ResolvePools(working, issues);
            var rotating = ResolveRotatingLanes(working, roster, issues);
            var anchors = ResolveAnchorLanes(working, roster, pools, issues);
            ValidateAccountLanes(rotating, anchors, issues);
            var templates = ResolveTemplates(working, currentPlannerGroups, issues);

            if (issues.Any(static issue => issue.IsBlocking))
            {
                return CreatePreview(
                    sourceConfigurationFingerprint,
                    [],
                    [],
                    [],
                    [],
                    issues);
            }

            var crews = BuildCrews(pools, rotating, anchors, issues, out var unusedCounts);
            var planCount = checked(crews.Count * templates.Count);
            if (planCount > DadPresetBatchLimits.MaxPlannerGroups)
            {
                issues.Add(Block(
                    "dad-batch-plan-limit",
                    $"The draft would create {planCount} Plans; the limit is {DadPresetBatchLimits.MaxPlannerGroups}."));
            }
            if (working.CreateCombinedSchedule && planCount > DadPresetBatchLimits.MaxScheduleEntries)
            {
                issues.Add(Block(
                    "dad-batch-combined-entry-limit",
                    $"The combined Schedule would contain {planCount} entries; the limit is {DadPresetBatchLimits.MaxScheduleEntries}."));
            }
            if (crews.Count > DadPresetBatchLimits.MaxScheduleEntries)
            {
                issues.Add(Block(
                    "dad-batch-template-entry-limit",
                    $"Each template Schedule would contain {crews.Count} entries; the limit is {DadPresetBatchLimits.MaxScheduleEntries}."));
            }
            if (issues.Any(static issue => issue.IsBlocking))
            {
                return CreatePreview(
                    sourceConfigurationFingerprint,
                    crews,
                    unusedCounts,
                    [],
                    [],
                    issues);
            }

            var timestamp = EnsureUtc(nowUtc ?? DateTime.UtcNow);
            var plans = new List<DadPlannerGroup>(planCount);
            var plansByKey = new Dictionary<(string TemplateId, string PoolId, int CrewIndex), DadPlannerGroup>();
            foreach (var template in templates)
            {
                foreach (var crew in crews)
                {
                    var plan = BuildPlan(template.Selection, template.Source, crew, timestamp);
                    plans.Add(plan);
                    plansByKey[(template.Selection.PlannerGroupId, crew.PoolId, crew.CrewIndex)] = plan;
                }
            }

            var schedules = new List<DadScheduleDefinition>();
            foreach (var template in templates)
            {
                var templatePlans = crews
                    .Select(crew => plansByKey[(template.Selection.PlannerGroupId, crew.PoolId, crew.CrewIndex)])
                    .ToList();
                schedules.Add(BuildTemplateSchedule(template.Selection, templatePlans, timestamp));
            }

            if (working.CreateCombinedSchedule)
            {
                var combinedPlans = new List<DadPlannerGroup>(planCount);
                foreach (var pool in pools)
                {
                    for (var crewIndex = 1; crewIndex <= pool.Source.CrewCount; crewIndex++)
                    {
                        foreach (var template in templates)
                        {
                            combinedPlans.Add(plansByKey[(
                                template.Selection.PlannerGroupId,
                                pool.PoolId,
                                crewIndex)]);
                        }
                    }
                }
                schedules.Add(BuildCombinedSchedule(working, combinedPlans, timestamp));
            }

            ValidateGeneratedCollisions(
                plans,
                schedules,
                currentPlannerGroups,
                currentSchedules,
                issues);
            return CreatePreview(
                sourceConfigurationFingerprint,
                crews,
                unusedCounts,
                plans,
                schedules,
                issues);
        }
    }

    public DadPresetBatchMutationResult Apply(DadPresetBatchPreview? preview)
    {
        lock (transactionGate)
        {
            if (preview == null)
                return Failure("dad-batch-preview-missing", "Build and review a batch preview before applying it.");
            if (!preview.CanApply)
                return Failure("dad-batch-preview-invalid", preview.Summary);

            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-batch-mutation-locked", blocker);

            configuration.PlannerGroups ??= [];
            configuration.Schedules ??= [];
            var currentFingerprint = BuildConfigurationFingerprint(configuration.PlannerGroups, configuration.Schedules);
            if (!string.Equals(currentFingerprint, preview.SourceConfigurationFingerprint, StringComparison.Ordinal))
            {
                return Failure(
                    "dad-batch-preview-stale",
                    "Plans or Schedules changed after preview. Refresh the preview before applying.");
            }

            var exactPreviewFingerprint = BuildPreviewFingerprint(
                preview.SourceConfigurationFingerprint,
                preview.Crews,
                preview.PlannerGroups,
                preview.Schedules);
            if (!string.Equals(exactPreviewFingerprint, preview.Fingerprint, StringComparison.Ordinal))
                return Failure("dad-batch-preview-changed", "The frozen preview changed after it was built. Refresh it before applying.");

            var previousGroups = ClonePlannerGroups(configuration.PlannerGroups);
            var previousSchedules = CloneSchedules(configuration.Schedules);
            var previousUndo = undoSnapshot;
            var nextGroups = previousGroups
                .Concat(preview.PlannerGroups.Select(ClonePlannerGroup))
                .ToList();
            var nextSchedules = previousSchedules
                .Concat(preview.Schedules.Select(static schedule => schedule.Clone()))
                .ToList();
            var token = Guid.NewGuid().ToString("N");

            try
            {
                configuration.PlannerGroups = nextGroups;
                configuration.Schedules = nextSchedules;
                saveConfiguration();
                undoSnapshot = new UndoSnapshot(
                    token,
                    previousGroups,
                    previousSchedules,
                    BuildConfigurationFingerprint(nextGroups, nextSchedules));
            }
            catch (Exception exception)
            {
                configuration.PlannerGroups = previousGroups;
                configuration.Schedules = previousSchedules;
                undoSnapshot = previousUndo;
                return Failure(
                    "dad-batch-save-failed",
                    $"Batch apply was rolled back: {exception.GetType().Name}.");
            }

            return new DadPresetBatchMutationResult(
                true,
                "dad-batch-applied",
                $"Appended {preview.PlannerGroups.Count} Plan(s) and {preview.Schedules.Count} Schedule(s) atomically.",
                token,
                preview.PlannerGroups.Count,
                preview.Schedules.Count);
        }
    }

    public DadPresetBatchMutationResult Undo(string? token = null)
    {
        lock (transactionGate)
        {
            var blocker = GetMutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocker))
                return Failure("dad-batch-mutation-locked", blocker);
            if (undoSnapshot == null)
                return Failure("dad-batch-undo-unavailable", "No batch apply is available to undo in this plugin session.");
            if (!string.IsNullOrWhiteSpace(token) &&
                !string.Equals(undoSnapshot.Token, token.Trim(), StringComparison.Ordinal))
            {
                return Failure("dad-batch-undo-token-mismatch", "The batch undo token does not match the last apply.");
            }

            configuration.PlannerGroups ??= [];
            configuration.Schedules ??= [];
            var currentFingerprint = BuildConfigurationFingerprint(configuration.PlannerGroups, configuration.Schedules);
            if (!string.Equals(currentFingerprint, undoSnapshot.PostApplyFingerprint, StringComparison.Ordinal))
            {
                return Failure(
                    "dad-batch-undo-drift",
                    "Plans or Schedules changed after Apply. Exact undo was refused to preserve newer work.");
            }

            var currentGroups = ClonePlannerGroups(configuration.PlannerGroups);
            var currentSchedules = CloneSchedules(configuration.Schedules);
            var snapshot = undoSnapshot;
            try
            {
                configuration.PlannerGroups = ClonePlannerGroups(snapshot.PlannerGroups);
                configuration.Schedules = CloneSchedules(snapshot.Schedules);
                saveConfiguration();
                undoSnapshot = null;
            }
            catch (Exception exception)
            {
                configuration.PlannerGroups = currentGroups;
                configuration.Schedules = currentSchedules;
                undoSnapshot = snapshot;
                return Failure(
                    "dad-batch-undo-save-failed",
                    $"Batch undo was rolled back: {exception.GetType().Name}.");
            }

            return new DadPresetBatchMutationResult(
                true,
                "dad-batch-undone",
                "Restored the exact pre-apply Plan and Schedule collections.");
        }
    }

    private void ValidateDraftShape(DadPresetBatchDraft draft, ICollection<DadPresetBatchIssue> issues)
    {
        if (draft.RotatingLanes.Count == 0)
            issues.Add(Block("dad-batch-rotating-empty", "Select at least one rotating account lane."));
        if (draft.AnchorLanes.Count == 0)
            issues.Add(Block("dad-batch-anchor-empty", "Select at least one anchor account lane."));
        if (draft.RotatingLanes.Count + draft.AnchorLanes.Count > DadPresetBatchLimits.MaxAccountLanes)
            issues.Add(Block("dad-batch-lane-limit", $"At most {DadPresetBatchLimits.MaxAccountLanes} account lanes are supported."));
        if (draft.Pools.Count == 0)
            issues.Add(Block("dad-batch-pool-empty", "Create at least one named data-center pool."));
        if (draft.Pools.Count > DadPresetBatchLimits.MaxPools)
            issues.Add(Block("dad-batch-pool-limit", $"At most {DadPresetBatchLimits.MaxPools} pools are supported."));
        if (draft.Templates.Count == 0)
            issues.Add(Block("dad-batch-template-empty", "Select at least one ordinary Plan template."));
        if (draft.Templates.Count > DadPresetBatchLimits.MaxTemplates)
            issues.Add(Block("dad-batch-template-limit", $"At most {DadPresetBatchLimits.MaxTemplates} templates are supported."));
        if (!Enum.IsDefined(draft.CombinedScheduleCadence))
            issues.Add(Block("dad-batch-combined-cadence-invalid", "The combined Schedule cadence is invalid."));
        if (draft.CreateCombinedSchedule && !IsValidText(draft.CombinedScheduleName))
            issues.Add(Block("dad-batch-combined-name-invalid", "The combined Schedule requires a name of 1-128 characters."));
    }

    private static List<ResolvedPool> ResolvePools(
        DadPresetBatchDraft draft,
        ICollection<DadPresetBatchIssue> issues)
    {
        var pools = new List<ResolvedPool>();
        var poolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var poolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignedDataCenters = new HashSet<uint>();
        foreach (var source in draft.Pools)
        {
            var poolId = NormalizeId(source.PoolId);
            var displayName = NormalizeText(source.DisplayName);
            if (string.IsNullOrWhiteSpace(poolId) || !poolIds.Add(poolId))
                issues.Add(Block("dad-batch-pool-id-invalid", "Pool IDs must be non-empty and unique."));
            if (!IsValidText(displayName) || !poolNames.Add(displayName))
                issues.Add(Block("dad-batch-pool-name-invalid", "Pool names must be unique and contain 1-128 characters."));
            if (source.DataCenterIds.Count == 0 || source.DataCenterIds.Any(static id => id == 0) ||
                source.DataCenterIds.Distinct().Count() != source.DataCenterIds.Count)
            {
                issues.Add(Block("dad-batch-pool-dc-invalid", $"Pool '{displayName}' requires unique positive data-center IDs."));
            }
            foreach (var dataCenterId in source.DataCenterIds)
            {
                if (!assignedDataCenters.Add(dataCenterId))
                    issues.Add(Block("dad-batch-pool-dc-overlap", $"Data center {dataCenterId} belongs to more than one pool."));
            }
            if (source.CrewCount <= 0 || source.CrewCount > DadPresetBatchLimits.MaxPlannerGroups)
                issues.Add(Block("dad-batch-pool-count-invalid", $"Pool '{displayName}' has an invalid crew count."));
            pools.Add(new ResolvedPool(source, poolId, displayName));
        }
        return pools;
    }

    private static List<ResolvedRotatingLane> ResolveRotatingLanes(
        DadPresetBatchDraft draft,
        DadAccountRosterCatalog catalog,
        ICollection<DadPresetBatchIssue> issues)
    {
        var lanes = new List<ResolvedRotatingLane>();
        var selectedCharacters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in draft.RotatingLanes)
        {
            if (lane.AccountKey.IsEmpty)
                issues.Add(Block("dad-batch-rotating-account-empty", "Every rotating lane requires an exact account."));
            if (lane.Characters.Count == 0 || lane.Characters.Count > DadPresetBatchLimits.MaxCharactersPerRotatingLane)
                issues.Add(Block("dad-batch-rotating-selection-invalid", $"Rotating account '{lane.AccountKey}' requires 1-{DadPresetBatchLimits.MaxCharactersPerRotatingLane} selected characters."));

            var resolved = new List<DadRosterCharacter>();
            foreach (var reference in lane.Characters)
            {
                var character = ResolveExactCharacter(catalog, reference, lane.AccountKey, "rotating", issues);
                if (character == null)
                    continue;
                var key = DadRosterIdentity.BuildKey(character);
                if (!selectedCharacters.Add(key))
                    issues.Add(Block("dad-batch-rotating-character-duplicate", $"Rotating character '{character.CharacterKey}' is selected more than once."));
                resolved.Add(character);
            }
            lanes.Add(new ResolvedRotatingLane(lane.AccountKey, resolved));
        }
        return lanes;
    }

    private static List<ResolvedAnchorLane> ResolveAnchorLanes(
        DadPresetBatchDraft draft,
        DadAccountRosterCatalog catalog,
        IReadOnlyList<ResolvedPool> pools,
        ICollection<DadPresetBatchIssue> issues)
    {
        var lanes = new List<ResolvedAnchorLane>();
        foreach (var lane in draft.AnchorLanes)
        {
            if (lane.AccountKey.IsEmpty)
                issues.Add(Block("dad-batch-anchor-account-empty", "Every anchor lane requires an exact account."));
            var assignments = new Dictionary<string, DadRosterCharacter>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in pools)
            {
                var matches = lane.Assignments
                    .Where(assignment => string.Equals(NormalizeId(assignment.PoolId), pool.PoolId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1)
                {
                    issues.Add(Block("dad-batch-anchor-assignment-invalid", $"Anchor account '{lane.AccountKey}' requires exactly one character for pool '{pool.DisplayName}'."));
                    continue;
                }
                var character = ResolveExactCharacter(catalog, matches[0].Character, lane.AccountKey, "anchor", issues);
                if (character == null)
                    continue;
                assignments[pool.PoolId] = character;
                if (!character.DataCenterId.HasValue || !pool.Source.DataCenterIds.Contains(character.DataCenterId.Value))
                {
                    issues.Add(new DadPresetBatchIssue(
                        "dad-batch-anchor-outside-pool",
                        $"Anchor '{character.CharacterKey}' is outside pool '{pool.DisplayName}' and will still be reused there.",
                        DadPresetBatchIssueSeverity.Warning));
                }
            }
            var unknownPoolAssignments = lane.Assignments.Count(assignment =>
                pools.All(pool => !string.Equals(pool.PoolId, NormalizeId(assignment.PoolId), StringComparison.OrdinalIgnoreCase)));
            if (unknownPoolAssignments > 0)
                issues.Add(Block("dad-batch-anchor-pool-missing", "An anchor assignment references a missing pool."));
            lanes.Add(new ResolvedAnchorLane(lane.AccountKey, assignments));
        }
        return lanes;
    }

    private static void ValidateAccountLanes(
        IReadOnlyList<ResolvedRotatingLane> rotating,
        IReadOnlyList<ResolvedAnchorLane> anchors,
        ICollection<DadPresetBatchIssue> issues)
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in rotating.Select(static lane => lane.AccountKey)
                     .Concat(anchors.Select(static lane => lane.AccountKey)))
        {
            if (account.IsEmpty || !accounts.Add(account.Value.Trim()))
                issues.Add(Block("dad-batch-account-lane-duplicate", "Rotating and anchor account lanes must be exact, unique, and disjoint."));
        }
    }

    private List<ResolvedTemplate> ResolveTemplates(
        DadPresetBatchDraft draft,
        IReadOnlyList<DadPlannerGroup> currentPlannerGroups,
        ICollection<DadPresetBatchIssue> issues)
    {
        var templates = new List<ResolvedTemplate>();
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredPrimaryCount = draft.RotatingLanes.Count + draft.AnchorLanes.Count;
        foreach (var selection in draft.Templates)
        {
            var templateId = NormalizeId(selection.PlannerGroupId);
            if (string.IsNullOrWhiteSpace(templateId) || !selectedIds.Add(templateId))
            {
                issues.Add(Block("dad-batch-template-id-invalid", "Selected template Plan IDs must be non-empty and unique."));
                continue;
            }
            var matches = currentPlannerGroups.Where(group => string.Equals(
                group.GroupId,
                templateId,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                issues.Add(Block("dad-batch-template-missing", $"Template Plan '{templateId}' is missing or ambiguous."));
                continue;
            }
            var source = matches[0];
            var normalizedSlots = DadPlannerSlotRules.NormalizeGroupSlots(source.Slots);
            var primarySlots = normalizedSlots.Where(static slot => !slot.IsSubstitute).ToList();
            if (primarySlots.Count != requiredPrimaryCount)
                issues.Add(Block("dad-batch-template-slot-count", $"Template '{source.DisplayName}' requires exactly {requiredPrimaryCount} primary slots; it has {primarySlots.Count}."));
            if (normalizedSlots.Any(static slot => slot.IsSubstitute))
                issues.Add(Block("dad-batch-template-substitutes", $"Template '{source.DisplayName}' contains substitutes. Batch generation accepts ordinary primary rows only."));
            if (!string.IsNullOrWhiteSpace(source.AutoPartyProposalId) || source.AutoPartyFormationOnly)
                issues.Add(Block("dad-batch-template-autoparty", $"Template '{source.DisplayName}' carries AutoParty proposal state and cannot be batch-cloned."));
            if (!IsValidText(selection.ActivityLabel) || !IsValidText(selection.PlanNameFormat))
                issues.Add(Block("dad-batch-template-name-format", $"Template '{source.DisplayName}' requires an activity label and Plan name format of 1-128 characters."));
            if (!IsValidText(ResolveScheduleName(selection)))
                issues.Add(Block("dad-batch-template-schedule-name", $"Template '{source.DisplayName}' requires a Schedule name of 1-128 characters."));
            if (!Enum.IsDefined(selection.ScheduleCadence))
                issues.Add(Block("dad-batch-template-cadence-invalid", $"Template '{source.DisplayName}' has an invalid Schedule cadence."));
            if (selection.RepeatCount is < DadScheduleRules.MinRepeatCount or > DadScheduleRules.MaxRepeatCount)
                issues.Add(Block("dad-batch-template-repeat-invalid", $"Template '{source.DisplayName}' has an invalid repeat count."));
            if (selection.SetDailyRewardChecksForAllPrimary && selection.ScheduleCadence != DadScheduleCadence.DailyReset)
                issues.Add(Block("dad-batch-daily-flags-require-daily", $"Template '{source.DisplayName}' can enable all Daily checks only with a DailyReset Schedule."));
            templates.Add(new ResolvedTemplate(selection.Clone(), source));
        }
        return templates;
    }

    private static List<DadPresetBatchCrew> BuildCrews(
        IReadOnlyList<ResolvedPool> pools,
        IReadOnlyList<ResolvedRotatingLane> rotating,
        IReadOnlyList<ResolvedAnchorLane> anchors,
        ICollection<DadPresetBatchIssue> issues,
        out List<DadPresetBatchUnusedCount> unusedCounts)
    {
        unusedCounts = [];
        var rotatingByPool = new Dictionary<(string PoolId, string AccountKey), List<DadRosterCharacter>>();
        foreach (var pool in pools)
        {
            foreach (var lane in rotating)
            {
                var ordered = lane.Characters
                    .Where(character => character.DataCenterId.HasValue && pool.Source.DataCenterIds.Contains(character.DataCenterId.Value))
                    .OrderBy(character => pool.Source.DataCenterIds.IndexOf(character.DataCenterId!.Value))
                    .ThenBy(static character => character.WorldName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static character => character.WorldId ?? uint.MaxValue)
                    .ThenBy(static character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static character => character.ContentId)
                    .ThenBy(static character => character.CharacterKey.Value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rotatingByPool[(pool.PoolId, lane.AccountKey.Value.Trim())] = ordered;
                unusedCounts.Add(new DadPresetBatchUnusedCount(
                    pool.PoolId,
                    lane.AccountKey,
                    ordered.Count,
                    Math.Min(ordered.Count, pool.Source.CrewCount)));
                if (ordered.Count < pool.Source.CrewCount)
                {
                    issues.Add(Block(
                        "dad-batch-pool-shortage",
                        $"Rotating account '{lane.AccountKey}' has {ordered.Count} selected character(s) in pool '{pool.DisplayName}', but {pool.Source.CrewCount} are required."));
                }
            }
        }
        if (issues.Any(static issue => issue.IsBlocking))
            return [];

        var crews = new List<DadPresetBatchCrew>();
        foreach (var pool in pools)
        {
            for (var index = 0; index < pool.Source.CrewCount; index++)
            {
                var characters = new List<DadRosterCharacterRef>();
                foreach (var lane in rotating)
                {
                    characters.Add(DadRosterIdentity.From(
                        rotatingByPool[(pool.PoolId, lane.AccountKey.Value.Trim())][index]));
                }
                foreach (var lane in anchors)
                    characters.Add(DadRosterIdentity.From(lane.Assignments[pool.PoolId]));
                crews.Add(new DadPresetBatchCrew(pool.PoolId, pool.DisplayName, index + 1, characters));
            }
        }
        return crews;
    }

    private static DadPlannerGroup BuildPlan(
        DadPresetBatchTemplate selection,
        DadPlannerGroup source,
        DadPresetBatchCrew crew,
        DateTime timestamp)
    {
        var primarySlots = DadPlannerSlotRules.GetPrimaryRows(source.Slots);
        var slots = primarySlots.Select((slot, index) =>
        {
            var clone = DadSchedulerGroupCloneRules.CloneSlot(slot);
            var character = crew.Characters[index];
            clone.RequiredAccountKey = character.AccountKey;
            clone.RequiredCharacterKey = character.CharacterKey;
            clone.SharedIdentity = null;
            clone.AllowSubstitution = false;
            if (selection.SetDailyRewardChecksForAllPrimary)
                clone.SkipIfDailyRouletteRewardReceived = true;
            return clone;
        }).ToList();
        var plan = DadSchedulerGroupCloneRules.CloneWithSlots(source, slots);
        var identity = crew.Characters.Select(DadRosterIdentity.BuildKey).ToArray();
        plan.GroupId = StableId([
            "dad-batch-plan-v1",
            selection.PlannerGroupId,
            crew.PoolId,
            crew.CrewIndex.ToString(CultureInfo.InvariantCulture),
            .. identity,
        ]);
        plan.DisplayName = FormatPlanName(selection.PlanNameFormat, selection.ActivityLabel, crew.PoolName, crew.CrewIndex);
        plan.SharedStopTargetIdentityToken = string.Empty;
        plan.AutoPartyProposalId = string.Empty;
        plan.AutoPartyFormationOnly = false;
        plan.IsTemplate = false;
        plan.ScheduleEnabled = false;
        plan.ScheduleCadenceHours = 0;
        plan.NextEligibleTimeUtc = null;
        plan.ScheduleRequester = string.Empty;
        plan.SchedulePriority = 0;
        plan.CreatedAtUtc = timestamp;
        plan.UpdatedAtUtc = timestamp;
        return plan;
    }

    private static DadScheduleDefinition BuildTemplateSchedule(
        DadPresetBatchTemplate selection,
        IReadOnlyList<DadPlannerGroup> plans,
        DateTime timestamp)
    {
        var scheduleId = StableId(
            "dad-batch-schedule-v1",
            selection.PlannerGroupId,
            string.Join('|', plans.Select(static plan => plan.GroupId)));
        return BuildSchedule(
            scheduleId,
            ResolveScheduleName(selection),
            selection.ScheduleCadence,
            selection.RepeatCount,
            plans,
            timestamp);
    }

    private static DadScheduleDefinition BuildCombinedSchedule(
        DadPresetBatchDraft draft,
        IReadOnlyList<DadPlannerGroup> plans,
        DateTime timestamp)
    {
        var scheduleId = StableId(
            "dad-batch-combined-schedule-v1",
            NormalizeText(draft.CombinedScheduleName),
            string.Join('|', plans.Select(static plan => plan.GroupId)));
        return BuildSchedule(
            scheduleId,
            NormalizeText(draft.CombinedScheduleName),
            draft.CombinedScheduleCadence,
            1,
            plans,
            timestamp);
    }

    private static DadScheduleDefinition BuildSchedule(
        string scheduleId,
        string displayName,
        DadScheduleCadence cadence,
        int repeatCount,
        IReadOnlyList<DadPlannerGroup> plans,
        DateTime timestamp)
        => new DadScheduleDefinition
        {
            SchemaVersion = 1,
            Revision = 1,
            ScheduleId = scheduleId,
            DisplayName = displayName,
            Cadence = cadence,
            Entries = plans.Select((plan, index) => new DadScheduleEntry
            {
                EntryId = StableId(
                    "dad-batch-entry-v1",
                    scheduleId,
                    index.ToString(CultureInfo.InvariantCulture),
                    plan.GroupId),
                GroupId = plan.GroupId,
                PresetName = plan.DisplayName,
                RepeatCount = repeatCount,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
            }).ToList(),
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
        }.Normalize();

    private void ValidateGeneratedCollisions(
        IReadOnlyList<DadPlannerGroup> plans,
        IReadOnlyList<DadScheduleDefinition> schedules,
        IReadOnlyList<DadPlannerGroup> currentPlannerGroups,
        IReadOnlyList<DadScheduleDefinition> currentSchedules,
        ICollection<DadPresetBatchIssue> issues)
    {
        var existingPlanIds = currentPlannerGroups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingPlanNames = currentPlannerGroups.Select(static group => group.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generatedPlanNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            if (!IsValidText(plan.DisplayName))
                issues.Add(Block("dad-batch-plan-name-invalid", "A generated Plan name is empty or exceeds 128 characters."));
            if (!generatedPlanIds.Add(plan.GroupId) || existingPlanIds.Contains(plan.GroupId))
                issues.Add(Block("dad-batch-plan-id-collision", $"Generated Plan ID '{plan.GroupId}' collides with another Plan."));
            if (!generatedPlanNames.Add(plan.DisplayName) || existingPlanNames.Contains(plan.DisplayName))
                issues.Add(Block("dad-batch-plan-name-collision", $"Generated Plan name '{plan.DisplayName}' collides with another Plan."));
        }

        var existingScheduleIds = currentSchedules.Select(static schedule => schedule.ScheduleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingScheduleNames = currentSchedules.Select(static schedule => schedule.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedScheduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generatedScheduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schedule in schedules)
        {
            if (!IsValidText(schedule.DisplayName))
                issues.Add(Block("dad-batch-schedule-name-invalid", "A generated Schedule name is empty or exceeds 128 characters."));
            if (!generatedScheduleIds.Add(schedule.ScheduleId) || existingScheduleIds.Contains(schedule.ScheduleId))
                issues.Add(Block("dad-batch-schedule-id-collision", $"Generated Schedule ID '{schedule.ScheduleId}' collides with another Schedule."));
            if (!generatedScheduleNames.Add(schedule.DisplayName) || existingScheduleNames.Contains(schedule.DisplayName))
                issues.Add(Block("dad-batch-schedule-name-collision", $"Generated Schedule name '{schedule.DisplayName}' collides with another Schedule."));
        }
    }

    private static DadRosterCharacter? ResolveExactCharacter(
        DadAccountRosterCatalog catalog,
        DadRosterCharacterRef reference,
        DadAccountKey expectedAccount,
        string laneKind,
        ICollection<DadPresetBatchIssue> issues)
    {
        if (reference.IsEmpty || expectedAccount.IsEmpty ||
            !DadRosterIdentity.SameAccount(reference.AccountKey, expectedAccount))
        {
            issues.Add(Block("dad-batch-character-reference-invalid", $"A {laneKind} character reference is empty or belongs to the wrong account."));
            return null;
        }
        var matches = catalog.Characters.Where(character => DadRosterIdentity.Matches(character, reference)).ToList();
        if (matches.Count != 1)
        {
            issues.Add(Block("dad-batch-character-not-exact", $"A {laneKind} character reference did not resolve to exactly one roster row."));
            return null;
        }
        var character = matches[0];
        if (character.Visibility != DadRosterVisibility.Active)
        {
            issues.Add(Block("dad-batch-character-inactive", $"Character '{character.CharacterKey}' is not Active in the roster."));
            return null;
        }
        if (!character.DataCenterId.HasValue || character.DataCenterId.Value == 0)
        {
            issues.Add(Block("dad-batch-character-dc-unknown", $"Character '{character.CharacterKey}' has no exact data-center ID."));
            return null;
        }
        return character;
    }

    private static DadPresetBatchPreview CreatePreview(
        string sourceConfigurationFingerprint,
        IReadOnlyList<DadPresetBatchCrew> crews,
        IReadOnlyList<DadPresetBatchUnusedCount> unusedCounts,
        IReadOnlyList<DadPlannerGroup> plans,
        IReadOnlyList<DadScheduleDefinition> schedules,
        IEnumerable<DadPresetBatchIssue> issues)
    {
        var finalIssues = issues.Distinct().ToList();
        return new DadPresetBatchPreview(
            BuildPreviewFingerprint(sourceConfigurationFingerprint, crews, plans, schedules),
            sourceConfigurationFingerprint,
            crews,
            unusedCounts,
            plans,
            schedules,
            finalIssues);
    }

    private static string BuildPreviewFingerprint(
        string sourceConfigurationFingerprint,
        IReadOnlyList<DadPresetBatchCrew> crews,
        IReadOnlyList<DadPlannerGroup> plans,
        IReadOnlyList<DadScheduleDefinition> schedules)
    {
        var builder = new StringBuilder();
        Append(builder, sourceConfigurationFingerprint, BuildConfigurationFingerprint(plans, schedules));
        foreach (var crew in crews)
        {
            Append(
                builder,
                crew.PoolId,
                crew.PoolName,
                crew.CrewIndex.ToString(CultureInfo.InvariantCulture));
            foreach (var character in crew.Characters)
                Append(builder, DadRosterIdentity.BuildKey(character));
        }
        return Hash(builder.ToString());
    }

    private static string BuildConfigurationFingerprint(
        IEnumerable<DadPlannerGroup>? groups,
        IEnumerable<DadScheduleDefinition>? schedules)
    {
        var groupList = ClonePlannerGroups(groups);
        var scheduleList = CloneSchedules(schedules);
        var builder = new StringBuilder(JsonSerializer.Serialize(new
        {
            PlannerGroups = groupList,
            Schedules = scheduleList,
        }));
        foreach (var group in groupList)
        {
            Append(
                builder,
                group.SharedStopTargetIdentityToken ?? string.Empty,
                group.AutoPartyProposalId ?? string.Empty,
                group.AutoPartyFormationOnly.ToString());
            foreach (var slot in group.Slots)
            {
                Append(
                    builder,
                    slot.SharedIdentity?.IdentityToken ?? string.Empty,
                    slot.SharedIdentity?.CharacterLabel ?? string.Empty,
                    (slot.SharedIdentity?.RequiresCharacter ?? false).ToString());
            }
        }
        return Hash(builder.ToString());
    }

    private static string FormatPlanName(string format, string activity, string pool, int index)
    {
        var value = NormalizeText(format)
            .Replace("{Activity}", NormalizeText(activity), StringComparison.OrdinalIgnoreCase)
            .Replace("{Pool}", NormalizeText(pool), StringComparison.OrdinalIgnoreCase)
            .Replace("{Index}", index.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        value = IndexFormatPattern.Replace(value, match =>
            index.ToString(new string('0', match.Groups["zeros"].Value.Length), CultureInfo.InvariantCulture));
        return NormalizeText(value);
    }

    private static string ResolveScheduleName(DadPresetBatchTemplate selection)
        => string.IsNullOrWhiteSpace(selection.ScheduleName)
            ? $"{NormalizeText(selection.ActivityLabel)} Batch"
            : NormalizeText(selection.ScheduleName);

    private string GetMutationBlocker()
    {
        try
        {
            return mutationBlocker()?.Trim() ?? string.Empty;
        }
        catch (Exception exception)
        {
            return $"Batch mutation readiness could not be verified ({exception.GetType().Name}).";
        }
    }

    private static DadPlannerGroup ClonePlannerGroup(DadPlannerGroup group)
        => DadSchedulerGroupCloneRules.CloneWithSlots(group, group.Slots);

    private static List<DadPlannerGroup> ClonePlannerGroups(IEnumerable<DadPlannerGroup>? groups)
        => (groups ?? []).Select(ClonePlannerGroup).ToList();

    private static List<DadScheduleDefinition> CloneSchedules(IEnumerable<DadScheduleDefinition>? schedules)
        => (schedules ?? []).Select(static schedule => schedule.Clone()).ToList();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string NormalizeId(string? value)
        => (value ?? string.Empty).Trim();

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim();

    private static bool IsValidText(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized.Length is > 0 and <= DadPresetBatchLimits.MaxTextLength;
    }

    private static DadPresetBatchIssue Block(string safeCode, string message)
        => new(safeCode, message, DadPresetBatchIssueSeverity.Blocking);

    private static DadPresetBatchMutationResult Failure(string safeCode, string summary)
        => new(false, safeCode, summary);

    private static string StableId(params string[] values)
    {
        var builder = new StringBuilder();
        Append(builder, values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32]
            .ToLowerInvariant();
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Append(StringBuilder builder, params string[] values)
    {
        foreach (var value in values)
            builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private sealed record ResolvedPool(DadPresetBatchPool Source, string PoolId, string DisplayName);
    private sealed record ResolvedRotatingLane(DadAccountKey AccountKey, IReadOnlyList<DadRosterCharacter> Characters);
    private sealed record ResolvedAnchorLane(DadAccountKey AccountKey, IReadOnlyDictionary<string, DadRosterCharacter> Assignments);
    private sealed record ResolvedTemplate(DadPresetBatchTemplate Selection, DadPlannerGroup Source);
    private sealed record UndoSnapshot(
        string Token,
        IReadOnlyList<DadPlannerGroup> PlannerGroups,
        IReadOnlyList<DadScheduleDefinition> Schedules,
        string PostApplyFingerprint);
}
