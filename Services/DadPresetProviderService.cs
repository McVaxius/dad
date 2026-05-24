using dad.Models;
using Lumina.Excel.Sheets;

namespace dad.Services;

public sealed class DadPresetProviderService
{
    private static readonly DadPlannerActivityMode[] PlannerActivityModes =
    [
        DadPlannerActivityMode.Msq,
        DadPlannerActivityMode.DutySupport,
        DadPlannerActivityMode.Trust,
        DadPlannerActivityMode.PremadeDuty,
        DadPlannerActivityMode.DutyPremade,
        DadPlannerActivityMode.DailyMsqPremade,
        DadPlannerActivityMode.Blunderville,
        DadPlannerActivityMode.Mogtome,
        DadPlannerActivityMode.Commendation,
        DadPlannerActivityMode.Astrope,
        DadPlannerActivityMode.LocalDuty,
        DadPlannerActivityMode.CustomDuty,
    ];

    private static readonly DadPlannerLaneDefinition[] PlannerLaneDefinitions =
    [
        new()
        {
            ActivityMode = DadPlannerActivityMode.Msq,
            RunFamily = DadPlannerRunFamily.Msq,
            ModuleId = DadModuleId.Msq,
            DisplayName = "MSQ",
            Summary = "Main scenario roulette/preset lane with Phase 5 readiness/status surfaced; live queue start remains deferred.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Policy deferred",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.LanParty,
            DefaultQueueAuthority = DadQueueAuthority.Leader,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            NextAction = "Keep live MSQ queue deferred until preset/roulette queue policy is proven.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.DutySupport,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.DutySupport,
            DisplayName = "Duty Support",
            Summary = "Solo Duty Support lane with selected Duty Finder content.",
            Maturity = DadLaneMaturity.LocalTestable,
            MaturityLabel = "Local test",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select Duty Support duty, then wire guarded RequestDutySupport/SendDutySupport submit.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Trust,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.Trust,
            DisplayName = "Trust",
            Summary = "Solo Trust lane with selected native Trust content.",
            Maturity = DadLaneMaturity.LocalTestable,
            MaturityLabel = "Local test",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select Trust-capable duty, then start guarded native Trust queue.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.PremadeDuty,
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ModuleId = DadModuleId.PremadeDuty,
            DisplayName = "Premade Duty",
            Summary = "Dad-owned full-party regular Duty Finder lane with guarded synced/unsynced queue start.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Guarded queue",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.LanParty,
            DefaultQueueAuthority = DadQueueAuthority.Leader,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            RequiresDutySelector = true,
            NextAction = "Manual-test full premade synced/unsynced starts, blockers, cancel, duty exit, and DTR/status text.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Blunderville,
            RunFamily = DadPlannerRunFamily.Event,
            ModuleId = DadModuleId.Blunderville,
            DisplayName = "Blunderville",
            Summary = "Gold Saucer Blunderville lane for configured per-character emote runs.",
            Maturity = DadLaneMaturity.PreviewOnly,
            MaturityLabel = "Preview",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.Blunderville,
            DefaultQueueAuthority = DadQueueAuthority.Blunderville,
            ExpectedPartySize = 1,
            UsesExternalHelper = true,
            NextAction = "Enter Blunderville, run configured emote, then fail/leave per character.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Mogtome,
            RunFamily = DadPlannerRunFamily.FarmLoops,
            ModuleId = DadModuleId.Mogtome,
            DisplayName = "MOGTOME",
            Summary = "Dad-owned MOGTOME helper lane using MOGTOME queue/requeue safety patterns.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Integration deferred",
            AccentColorHex = "#A855F7",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.Mogtome,
            DefaultQueueAuthority = DadQueueAuthority.Mogtome,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            UsesExternalHelper = true,
            NextAction = "Keep Dad authority, then wire narrow MOGTOME helper handoff.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Commendation,
            RunFamily = DadPlannerRunFamily.FarmLoops,
            ModuleId = DadModuleId.Commendation,
            DisplayName = "Commendation",
            Summary = "Short duty loop for commendation farming.",
            Maturity = DadLaneMaturity.PreviewOnly,
            MaturityLabel = "Preview",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.AuraFarmer,
            DefaultQueueAuthority = DadQueueAuthority.AuraFarmer,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            NextAction = "Reuse party queue base, then add commendation attempt detector.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Astrope,
            RunFamily = DadPlannerRunFamily.FarmLoops,
            ModuleId = DadModuleId.Astrope,
            DisplayName = "Astrope",
            Summary = "Timed Astrope farming window.",
            Maturity = DadLaneMaturity.PreviewOnly,
            MaturityLabel = "Preview",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.AuraFarmer,
            DefaultQueueAuthority = DadQueueAuthority.AuraFarmer,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            NextAction = "Reuse party queue base, then add time-window and attempt policy.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.LocalDuty,
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ModuleId = DadModuleId.Duty,
            DisplayName = "Local Duty / Unsync",
            Summary = "One-character Dad-owned regular Duty Finder lane with synced or unrestricted/unsynced queue mode.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Live queue",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Manual-test synced and unsynced starts, cancellation, duty exit, and DTR/status text.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.CustomDuty,
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ModuleId = DadModuleId.CustomDuty,
            DisplayName = "Custom Duty",
            Summary = "Typed custom Duty Finder lane for later specialized policies.",
            Maturity = DadLaneMaturity.MissingContract,
            MaturityLabel = "Needs duty selector",
            AccentColorHex = "#EF4444",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select duty and policy before this lane can start.",
        },
    ];

    private static readonly DadPlannerOperatorMode[] PlannerOperatorModes =
    [
        DadPlannerOperatorMode.RemotePartyPlan,
        DadPlannerOperatorMode.TestOnThisMachine,
    ];

    private static readonly PlannerSlotDefinition[] PartySlotDefinitions =
    [
        new("Tank", DadPartyRole.Tank, true),
        new("Healer", DadPartyRole.Healer, true),
        new("DPS 1", DadPartyRole.Dps, true),
        new("DPS 2", DadPartyRole.Dps, true),
    ];

    private readonly record struct PlannerSlotDefinition(string SlotId, DadPartyRole RequiredRole, bool AllowSubstitution);

    private readonly DadModuleRegistry moduleRegistry;
    private readonly Func<IReadOnlyList<DadRosterAccountOption>> accountDirectoryProvider;
    private IReadOnlyList<DadPlannerDutyOption>? plannerDutyCatalog;
    private IReadOnlyDictionary<uint, DadPlannerDutyOption>? plannerDutyCatalogById;

    public DadPresetProviderService(
        DadModuleRegistry moduleRegistry,
        Func<IReadOnlyList<DadRosterAccountOption>> accountDirectoryProvider)
    {
        this.moduleRegistry = moduleRegistry;
        this.accountDirectoryProvider = accountDirectoryProvider;
    }

    public IReadOnlyList<string> GetLanPartyPresets()
        => DadRunRequestOptions.LanPartyPresetStubs;

    public IReadOnlyList<string> GetSupportedJobHints()
        => DadRunRequestOptions.JobHintExamples;

    public IReadOnlyList<string> GetMogtomeDutyPolicies()
        => DadMogtomeDutyPolicies.All;

    public string GetMogtomeDutyPolicyLabel(string policy)
        => policy switch
        {
            DadMogtomeDutyPolicies.PresetHandoff => "Preset handoff",
            DadMogtomeDutyPolicies.PreservePresetDuty => "Preset duty policy",
            DadMogtomeDutyPolicies.PinnedDutySelection => "Pinned duty selection",
            _ => policy,
        };

    public IReadOnlyList<string> GetPlannerActivityModes()
        => PlannerActivityModes.Select(GetPlannerActivityModeLabel).ToArray();

    public IReadOnlyList<DadPlannerActivityMode> GetPlannerActivityModeOptions()
        => PlannerActivityModes;

    public IReadOnlyList<DadPlannerLaneDefinition> GetPlannerLaneDefinitions()
        => PlannerLaneDefinitions.Select(CloneLaneDefinition).ToArray();

    public IReadOnlyList<DadPlannerRunFamily> GetPlannerRunFamilies()
        =>
        [
            DadPlannerRunFamily.Msq,
            DadPlannerRunFamily.LevelingNpc,
            DadPlannerRunFamily.DutyFinder,
            DadPlannerRunFamily.FarmLoops,
            DadPlannerRunFamily.Event,
        ];

    public IReadOnlyList<DadPlannerLaneDefinition> GetPlannerSubmodes(DadPlannerRunFamily runFamily)
        => PlannerLaneDefinitions
            .Where(lane => lane.RunFamily == runFamily)
            .Select(CloneLaneDefinition)
            .ToArray();

    public DadPlannerLaneDefinition GetPlannerLaneDefinition(DadPlannerActivityMode activityMode)
        => CloneLaneDefinition(ResolveLaneDefinition(activityMode));

    public DadPlannerRunFamily GetPlannerRunFamily(DadPlannerActivityMode activityMode)
        => ResolveLaneDefinition(activityMode).RunFamily;

    public DadPlannerActivityMode GetDefaultPlannerSubmode(DadPlannerRunFamily runFamily)
        => PlannerLaneDefinitions.FirstOrDefault(lane => lane.RunFamily == runFamily)?.ActivityMode
           ?? DadPlannerActivityMode.Msq;

    public IReadOnlyList<DadPlannerOperatorMode> GetPlannerOperatorModeOptions()
        => PlannerOperatorModes;

    public IReadOnlyList<DadRosterAccountOption> GetPlannerAccountOptions(DadCharacterPool pool)
        => GetPlannerAccountOptions();

    public IReadOnlyList<DadRosterAccountOption> GetPlannerAccountOptions()
        => accountDirectoryProvider()
            .Where(static option => !option.AccountKey.IsEmpty)
            .Select(static option => option.Clone())
            .OrderByDescending(static option => option.IsLocal)
            .ThenBy(static option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.SourceClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<DadPlannerDutyOption> SearchPlannerDutyOptions(
        DadPlannerActivityMode activityMode,
        string search,
        int maxResults = 96)
    {
        var normalizedSearch = search?.Trim() ?? string.Empty;
        var tokens = string.IsNullOrWhiteSpace(normalizedSearch)
            ? []
            : normalizedSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = GetPlannerDutyCatalog()
            .Where(option => MatchesPlannerLaneDuty(option, activityMode))
            .Where(option => tokens.Length == 0 || tokens.All(token =>
                option.SearchText.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(1, maxResults))
            .ToList();

        return results;
    }

    public DadPlannerDutyOption? GetPlannerDutyOption(uint contentFinderConditionId)
    {
        if (contentFinderConditionId == 0)
            return null;

        return GetPlannerDutyCatalogById().TryGetValue(contentFinderConditionId, out var duty)
            ? duty
            : null;
    }

    public DadPlannerDutyOption? GetPlannerSelectedDuty(DadPresetPlannerOptions options)
    {
        NormalizePlannerOptions(options);
        if (options.DutyContentFinderConditionId == 0)
            return null;

        if (!GetPlannerDutyCatalogById().TryGetValue(options.DutyContentFinderConditionId, out var duty))
            return null;

        if (!string.Equals(options.DutyDisplayName, duty.DutyDisplayName, StringComparison.Ordinal))
            options.DutyDisplayName = duty.DutyDisplayName;

        if (options.DutyExpectedPartySize <= 0)
            options.DutyExpectedPartySize = Math.Max(1, duty.QueueSize);

        return duty;
    }

    public string GetPlannerAccountFilterLabel(DadCharacterPool pool, DadPresetPlannerOptions options)
    {
        NormalizePlannerOptions(options);
        return BuildAccountFilterSummary(pool, options);
    }

    public DadActivityPreset BuildPlannerPreview(
        DadCharacterPool pool,
        DadPresetPlannerOptions? options = null,
        DadPlannerGroup? selectedGroup = null)
    {
        options ??= new DadPresetPlannerOptions();
        NormalizePlannerOptions(options);
        var lane = ResolveLaneDefinition(options.ActivityMode);
        var selectedDuty = GetPlannerSelectedDuty(options);
        var dutySelectorBlocker = BuildDutySelectorBlocker(lane, selectedDuty);
        selectedGroup = NormalizeSelectedGroup(selectedGroup);

        var localCharacter = pool.Characters.FirstOrDefault(static candidate => candidate.Source == DadCharacterSource.LocalRuntime);
        var effectiveInviteAuthority = ResolveEffectiveInviteAuthority(options);
        var filterStats = BuildFilterStats(pool, localCharacter, options);
        var accountFilterSummary = BuildAccountFilterSummary(pool, options);
        var availableCharacters = BuildAvailableCharacters(pool, localCharacter, options, selectedGroup);

        var selectedCharacters = selectedGroup?.Slots.Count > 0
            ? BuildGroupSlotAssignments(availableCharacters, selectedGroup, lane)
            : BuildSlotAssignments(availableCharacters, lane);
        var stopPolicy = BuildResolvedStopPolicy(options.StopPolicy, selectedCharacters, availableCharacters);
        var stopPolicyBlockers = BuildStopPolicyBlockers(stopPolicy, selectedCharacters, availableCharacters);
        var groupBlockers = BuildPlannerGroupBlockers(selectedGroup, selectedCharacters);
        var leaderCandidate = selectedCharacters
                                  .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
                                  .Select(slot => availableCharacters.FirstOrDefault(character =>
                                      string.Equals(character.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase)))
                                  .FirstOrDefault(static character => character != null && IsConnectedForPlanning(character))
                              ?? availableCharacters.FirstOrDefault(static character => IsConnectedForPlanning(character));
        var missingRoleSlots = selectedCharacters
            .Where(static slot => string.IsNullOrWhiteSpace(slot.CharacterKey))
            .Select(static slot => slot.SlotId)
            .ToList();
        var requestedPartySize = ResolveRequestedPartySize(options, selectedDuty, lane);
        var missingDutySelector = !string.IsNullOrWhiteSpace(dutySelectorBlocker);
        var insufficientPlannerPartyShell = lane.RequiresRemoteParty && requestedPartySize > selectedCharacters.Count;
        var blocked = string.IsNullOrWhiteSpace(leaderCandidate?.CharacterKey)
                      || missingRoleSlots.Count > 0
                      || missingDutySelector
                      || insufficientPlannerPartyShell
                      || stopPolicyBlockers.Count > 0
                      || groupBlockers.Count > 0;
        var localCandidateCount = availableCharacters.Count(static character => character.Source == DadCharacterSource.LocalRuntime);
        var remoteCandidateCount = availableCharacters.Count(static character => character.Source == DadCharacterSource.PeerRuntime);

        var preset = new DadActivityPreset
        {
            PresetId = $"{options.ActivityMode}-{options.OperatorMode}",
            DisplayName = options.PresetName,
            SelectedPlannerGroupId = selectedGroup?.GroupId ?? string.Empty,
            SelectedPlannerGroupName = selectedGroup?.DisplayName ?? string.Empty,
            UsingPlannerGroup = selectedGroup != null,
            RunFamily = options.RunFamily,
            RunFamilyId = GetPlannerRunFamilyLabel(options.RunFamily),
            ActivityMode = options.ActivityMode,
            ActivityModeId = GetPlannerActivityModeLabel(options.ActivityMode),
            StopPolicy = stopPolicy,
            OperatorMode = options.OperatorMode,
            OperatorModeLabel = GetPlannerOperatorModeLabel(options.OperatorMode),
            TransportOwner = options.TransportOwner,
            InviteAuthority = effectiveInviteAuthority,
            QueueAuthority = options.QueueAuthority,
            LaneDefinition = CloneLaneDefinition(lane),
            RosterSource = options.ConnectedOnly
                ? DadRosterSourceMode.ConnectedOnly
                : DadRosterSourceMode.ConnectedAndXadb,
            AvailableCharacters = availableCharacters,
            SelectedCharacters = selectedCharacters,
            LeaderCharacterKey = leaderCandidate?.CharacterKey ?? string.Empty,
            LeaderStatusText = leaderCandidate == null
                ? "No connected leader candidate passed the current filters."
                : $"{leaderCandidate.CharacterKey} | {FormatReadiness(leaderCandidate.Readiness)} | {FormatFreshness(leaderCandidate)} | {GetCharacterSourceLabel(leaderCandidate.Source)}",
            PreviewOnly = options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine,
            PreviewScope = BuildPreviewScope(options, localCandidateCount, remoteCandidateCount, blocked),
            AccountFilterSummary = accountFilterSummary,
            FilterStats = filterStats,
            FilterSummary = BuildFilterSummary(filterStats),
        };

        if (leaderCandidate == null)
            preset.Blockers.Add("Missing connected leader.");

        if (missingRoleSlots.Count > 0)
            preset.Blockers.Add($"Missing role slots: {string.Join(", ", missingRoleSlots)}.");

        if (missingDutySelector)
            preset.Blockers.Add(dutySelectorBlocker);

        if (insufficientPlannerPartyShell)
            preset.Blockers.Add($"Selected duty needs party size {requestedPartySize}, but planner shell currently exposes only {selectedCharacters.Count} typed slot(s).");

        preset.Blockers.AddRange(stopPolicyBlockers);
        preset.Blockers.AddRange(groupBlockers);

        if (selectedGroup != null)
        {
            preset.Notes.Add($"Planner group selected: {selectedGroup.DisplayName} ({selectedGroup.Slots.Count} slot(s)).");
            var offlinePreviewSlots = selectedCharacters
                .Where(static slot => slot.SelectedSource == DadCharacterSource.XadbOnly)
                .Select(static slot => slot.SlotId)
                .ToList();
            if (offlinePreviewSlots.Count > 0)
                preset.Notes.Add($"Offline/XADB-only preview slot(s): {string.Join(", ", offlinePreviewSlots)}. These rows are design-time only and will block live start until connected.");
        }

        if (blocked)
        {
            if (filterStats.ExcludedByStaleFilter > 0)
                preset.Blockers.Add($"Stale-only candidates filtered out: {filterStats.ExcludedByStaleFilter}.");

            if (filterStats.ExcludedByLocalOnlyIsolation > 0)
                preset.Blockers.Add($"Local-only isolation filtered out: {filterStats.ExcludedByLocalOnlyIsolation}.");

            if (filterStats.ExcludedByDatacenterFilter > 0)
                preset.Blockers.Add($"Datacenter filter excluded {filterStats.ExcludedByDatacenterFilter} candidate(s).");

            if (filterStats.ExcludedByAccountFilter > 0)
                preset.Blockers.Add($"Account filter excluded {filterStats.ExcludedByAccountFilter} candidate(s).");
        }

        if (options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine)
        {
            preset.Notes.Add(remoteCandidateCount == 0
                ? "Preview-only: remote participants are still missing; this mode validates local worker state, route selection, and queue/transport choices only."
                : "Preview-only: this mode focuses on what the local machine can validate before a full remote party retest.");
        }

        if (selectedDuty != null && lane.RequiresDutySelector)
            preset.Notes.Add($"Typed duty: {selectedDuty.SelectionLabel} | {selectedDuty.MetadataSummary}");

        preset.Notes.Add($"Stop policy: {stopPolicy.Describe()}.");

        if (filterStats.ExcludedByConnectedFilter > 0)
            preset.Notes.Add($"Connected filter removed {filterStats.ExcludedByConnectedFilter} candidate(s).");

        if (filterStats.ExcludedByAccountFilter > 0)
            preset.Notes.Add($"Account filter removed {filterStats.ExcludedByAccountFilter} candidate(s).");

        if (filterStats.ExcludedByPeerEligibility > 0)
            preset.Notes.Add($"Peer readiness filter removed {filterStats.ExcludedByPeerEligibility} candidate(s).");

        preset.ValidationState = ResolveValidationState(options, blocked, localCandidateCount);
        preset.ValidationSummary = BuildValidationSummary(preset, options, missingRoleSlots.Count, localCandidateCount, remoteCandidateCount);
        preset.PlannerSummary = BuildPlannerSummary(preset);
        return preset;
    }

    public string BuildPlannerSummary(DadCharacterPool pool, DadPresetPlannerOptions? options = null)
        => BuildPlannerPreview(pool, options).PlannerSummary;

    public DadPlannerRunRequestPreview BuildPlannerRunRequestPreview(
        DadCharacterPool pool,
        DadPresetPlannerOptions? options = null,
        string? requestId = null,
        DateTime? requestedAtUtc = null,
        DadActivityPreset? plannerPreviewOverride = null,
        DadPlannerGroup? selectedGroup = null)
    {
        options ??= new DadPresetPlannerOptions();
        NormalizePlannerOptions(options);
        selectedGroup = NormalizeSelectedGroup(selectedGroup);
        var lane = ResolveLaneDefinition(options.ActivityMode);
        var requestModuleId = ResolvePlannerModuleIdForRequest(options.ActivityMode, lane);
        var selectedDuty = GetPlannerSelectedDuty(options);
        var dutySelectorBlocker = BuildDutySelectorBlocker(lane, selectedDuty);
        var requestedPartySize = ResolveRequestedPartySize(options, selectedDuty, lane);
        var capability = moduleRegistry.GetCapability(requestModuleId);

        var plannerPreview = plannerPreviewOverride ?? BuildPlannerPreview(pool, options, selectedGroup);
        var selectedCharacters = ResolveSelectedCharacters(plannerPreview);
        var previewOnly = options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine;
        var request = new DadRunRequest
        {
            RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
            RequestedAtUtc = requestedAtUtc ?? DateTime.UtcNow,
            RequestedBy = previewOnly ? "planner-preview" : selectedGroup == null ? "planner" : $"planner-group:{selectedGroup.DisplayName}",
            StopPolicy = plannerPreview.StopPolicy.Clone().Normalize(),
            Orchestration = BuildPlannerOrchestration(options, plannerPreview, selectedCharacters, previewOnly, selectedDuty, selectedGroup),
        };

        PopulatePlannerRequestTask(request, options, selectedDuty, requestedPartySize);
        request.ApplyOrchestrationDefaults();

        var result = new DadPlannerRunRequestPreview
        {
            PlannerPreview = plannerPreview,
            Request = request,
            StopPolicy = request.StopPolicy.Clone(),
            ModuleId = requestModuleId,
            QueueAuthority = IsLocalNpcLane(options.ActivityMode)
                ? DadQueueAuthority.LocalOnly
                : options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine
                ? DadQueueAuthority.LocalOnly
                : options.QueueAuthority,
            ExpectedPartySize = IsLocalNpcLane(options.ActivityMode)
                ? 1
                : options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine
                    ? 1
                    : requestedPartySize,
            ModuleBlockers = capability.Blockers.Select(static blocker => blocker.Clone()).ToList(),
        };

        if (!string.IsNullOrWhiteSpace(dutySelectorBlocker))
        {
            result.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestModuleId,
                Capability = "DutySelector",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = dutySelectorBlocker,
            });
            BlockRequest(result, dutySelectorBlocker);
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (plannerPreview.ValidationState == DadReadinessState.Blocked)
        {
            BlockRequest(result, BuildPlannerBlockerSummary(plannerPreview));
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (selectedCharacters.Count == 0)
        {
            BlockRequest(result, "Planner request needs at least one selected typed character.");
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (previewOnly)
        {
            result.CanStart = false;
            result.StatusSummary = "Preview-only request built. Local validation only; remote start remains disabled.";
            result.BlockedReason = plannerPreview.Blockers.Count == 0
                ? "Preview-only mode keeps remote start disabled."
                : BuildPlannerBlockerSummary(plannerPreview);
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        var startCapabilityBlocker = capability.Blockers.FirstOrDefault(static blocker =>
            string.Equals(blocker.Capability, "CanStartQueue", StringComparison.OrdinalIgnoreCase));
        if (startCapabilityBlocker != null)
        {
            result.CanStart = false;
            result.StatusSummary = $"Planner request built, but start is blocked by module capability: {startCapabilityBlocker.Summary}";
            result.BlockedReason = startCapabilityBlocker.Summary;
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        var nonLiveSelections = selectedCharacters
            .Where(static character => !IsConnectedForPlanning(character))
            .Select(static character => character.CharacterKey)
            .ToList();
        if (nonLiveSelections.Count > 0)
        {
            result.CanStart = false;
            result.StatusSummary = $"Planner request built, but start is blocked by non-live selection(s): {string.Join(", ", nonLiveSelections)}.";
            result.BlockedReason = result.StatusSummary;
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        result.CanStart = true;
        result.StatusSummary = "Planner request ready to start.";
        PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
        return result;
    }

    private static void PopulatePlannerRequestTask(
        DadRunRequest request,
        DadPresetPlannerOptions options,
        DadPlannerDutyOption? selectedDuty,
        int requestedPartySize)
    {
        switch (options.ActivityMode)
        {
            case DadPlannerActivityMode.Msq:
                request.Msq = new DadMsqTask
                {
                    Preset = "MSQ",
                    LegacyQueuePreset = "Daily MSQ",
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.DailyMsqPremade:
                request.DailyMsq = new DadDailyMsqTask
                {
                    LanPartyPreset = "Daily MSQ",
                };
                break;
            case DadPlannerActivityMode.DutySupport:
                request.DutySupport = new DadDutySupportTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Trust:
                request.Trust = new DadTrustTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.PremadeDuty:
            case DadPlannerActivityMode.DutyPremade:
                request.PremadeDuty = new DadPremadeDutyTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Unsynced = options.DutyUnsynced,
                    ExpectedPartySize = requestedPartySize,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Blunderville:
                request.Blunderville = new DadBlundervilleTask
                {
                    Mode = DadBlundervilleModes.FixedEmoteRun,
                    CompletionPolicy = DadBlundervillePolicies.FailOrLeaveAfterEmote,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Mogtome:
                request.Mogtome = new DadMogtomeTask
                {
                    Preset = string.IsNullOrWhiteSpace(options.MogtomePreset) ? "Daily MSQ" : options.MogtomePreset,
                    DutyPolicy = string.IsNullOrWhiteSpace(options.MogtomeDutyPolicy)
                        ? DadMogtomeDutyPolicies.PresetHandoff
                        : options.MogtomeDutyPolicy,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Commendation:
                request.Commendation = new DadCommendationTask
                {
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Astrope:
                request.Astrope = new DadAstropeTask
                {
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.LocalDuty:
                request.Dungeon = new DadDungeonTask
                {
                    Count = 1,
                    Frequency = DadRunRequestOptions.FrequencyPerArRun,
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    SelectedDungeon = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    ExecutionPreference = DadRunRequestOptions.TrustThenDutySupport,
                    Unsynced = options.DutyUnsynced,
                };
                break;
            case DadPlannerActivityMode.CustomDuty:
                request.CustomDuty = new DadCustomDutyTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                };
                break;
        }
    }

    public string GetLanPartyPresetsJson()
        => DadIpcJson.Serialize(GetLanPartyPresets());

    public string GetSupportedJobHintsJson()
        => DadIpcJson.Serialize(GetSupportedJobHints());

    public string GetPlannerActivityModeLabel(DadPlannerActivityMode activityMode)
        => activityMode switch
        {
            DadPlannerActivityMode.Msq or DadPlannerActivityMode.DailyMsqPremade => "MSQ",
            DadPlannerActivityMode.DutySupport => "Duty Support",
            DadPlannerActivityMode.Trust => "Trust",
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => "Premade Duty",
            DadPlannerActivityMode.Blunderville => "Blunderville",
            DadPlannerActivityMode.Mogtome => "MOGTOME",
            DadPlannerActivityMode.Commendation => "Commendation",
            DadPlannerActivityMode.Astrope => "Astrope",
            DadPlannerActivityMode.LocalDuty => "Local Duty / Unsync",
            DadPlannerActivityMode.CustomDuty => "Custom Duty",
            _ => activityMode.ToString(),
        };

    public string GetPlannerRunFamilyLabel(DadPlannerRunFamily runFamily)
        => runFamily switch
        {
            DadPlannerRunFamily.Msq => "MSQ",
            DadPlannerRunFamily.LevelingNpc => "Leveling / NPC",
            DadPlannerRunFamily.DutyFinder => "Duty Finder",
            DadPlannerRunFamily.FarmLoops => "Farm Loops",
            DadPlannerRunFamily.Event => "Event",
            _ => runFamily.ToString(),
        };

    public string GetPlannerStopModeLabel(DadPlannerStopMode stopMode)
        => stopMode switch
        {
            DadPlannerStopMode.TargetLevel => "Target level",
            _ => "After runs",
        };

    public string GetPlannerOperatorModeLabel(DadPlannerOperatorMode operatorMode)
        => operatorMode switch
        {
            DadPlannerOperatorMode.RemotePartyPlan => "Remote Party Plan",
            DadPlannerOperatorMode.TestOnThisMachine => "Test On This Machine",
            _ => operatorMode.ToString(),
        };

    public string GetTransportOwnerLabel(DadTransportOwner owner)
        => owner switch
        {
            DadTransportOwner.DadDirect => "Dad duty lane",
            DadTransportOwner.LanParty => "Dad premade lane",
            DadTransportOwner.AuraFarmer => "Dad aura lane",
            DadTransportOwner.Mogtome => "MOGTOME",
            DadTransportOwner.Blunderville => "Blunderville",
            DadTransportOwner.External => "External",
            _ => owner.ToString(),
        };

    public string GetQueueAuthorityLabel(DadQueueAuthority authority)
        => authority switch
        {
            DadQueueAuthority.LocalOnly => "Local Only",
            DadQueueAuthority.Leader => "Server Dad",
            DadQueueAuthority.DadDirect => "Dad duty lane",
            DadQueueAuthority.LanParty => "Dad premade lane",
            DadQueueAuthority.AuraFarmer => "Dad aura lane",
            DadQueueAuthority.Mogtome => "MOGTOME",
            DadQueueAuthority.Blunderville => "Blunderville",
            _ => authority.ToString(),
        };

    public string GetInviteAuthorityLabel(DadInviteAuthority authority)
        => authority switch
        {
            DadInviteAuthority.NotNeeded => "Not needed",
            DadInviteAuthority.PresetLeader => "Preset leader",
            DadInviteAuthority.ServerDad => "Server Dad",
            DadInviteAuthority.External => "External",
            _ => authority.ToString(),
        };

    public string GetEffectiveInviteAuthorityLabel(DadPresetPlannerOptions options)
        => GetInviteAuthorityLabel(ResolveEffectiveInviteAuthority(options));

    public string GetRosterSourceLabel(DadRosterSourceMode rosterSource)
        => rosterSource switch
        {
            DadRosterSourceMode.ConnectedAndXadb => "Connected + XADB",
            DadRosterSourceMode.ConnectedOnly => "Connected only",
            DadRosterSourceMode.XadbOnly => "XADB only",
            _ => rosterSource.ToString(),
        };

    public string GetCharacterSourceLabel(DadCharacterSource source)
        => source switch
        {
            DadCharacterSource.LocalRuntime => "Local runtime",
            DadCharacterSource.PeerRuntime => "Peer runtime",
            DadCharacterSource.XadbOnly => "XADB only",
            DadCharacterSource.ManualUnresolved => "Manual unresolved",
            _ => source.ToString(),
        };

    public static DadPartyRole ClassifyRole(DadAcquiredCharacter character)
    {
        var job = character.CurrentJobAbbrev.Trim().ToUpperInvariant();
        return job switch
        {
            "PLD" or "WAR" or "DRK" or "GNB" => DadPartyRole.Tank,
            "WHM" or "SCH" or "AST" or "SGE" => DadPartyRole.Healer,
            "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" => DadPartyRole.Melee,
            "BRD" or "MCH" or "DNC" => DadPartyRole.PhysicalRanged,
            "BLM" or "SMN" or "RDM" or "PCT" => DadPartyRole.Caster,
            "BLU" => DadPartyRole.Limited,
            _ => DadPartyRole.Any,
        };
    }

    private void NormalizePlannerOptions(DadPresetPlannerOptions options)
    {
        options.SelectedPlannerGroupId = options.SelectedPlannerGroupId?.Trim() ?? string.Empty;
        options.RunFamily = ResolveLaneDefinition(options.ActivityMode).RunFamily;
        options.ActivityName = options.ActivityMode switch
        {
            DadPlannerActivityMode.Msq or DadPlannerActivityMode.DailyMsqPremade => "MSQ",
            DadPlannerActivityMode.DutySupport => "Duty Support",
            DadPlannerActivityMode.Trust => "Trust",
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => "Premade Duty",
            DadPlannerActivityMode.Blunderville => "Blunderville",
            DadPlannerActivityMode.Mogtome => "MOGTOME",
            DadPlannerActivityMode.Commendation => "Commendation",
            DadPlannerActivityMode.Astrope => "Astrope",
            DadPlannerActivityMode.LocalDuty => "Local Duty / Unsync",
            DadPlannerActivityMode.CustomDuty => "Custom Duty",
            _ => options.ActivityName,
        };
        options.PresetName = options.ActivityMode switch
        {
            DadPlannerActivityMode.Msq or DadPlannerActivityMode.DailyMsqPremade => "MSQ Main Group",
            DadPlannerActivityMode.DutySupport => "Duty Support",
            DadPlannerActivityMode.Trust => "Trust",
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => "Premade Duty Group",
            DadPlannerActivityMode.Blunderville => "Blunderville",
            DadPlannerActivityMode.Mogtome => "MOGTOME Group",
            DadPlannerActivityMode.Commendation => "Commendation Group",
            DadPlannerActivityMode.Astrope => "Astrope Group",
            DadPlannerActivityMode.LocalDuty => "Local Duty",
            DadPlannerActivityMode.CustomDuty => "Custom Duty",
            _ => "Dad Planner",
        };
        var lane = ResolveLaneDefinition(options.ActivityMode);
        if (options.DutyExpectedPartySize <= 0 && options.DutyContentFinderConditionId == 0 && lane.ExpectedPartySize > 0)
            options.DutyExpectedPartySize = lane.ExpectedPartySize;
        if (IsLocalNpcLane(options.ActivityMode))
        {
            options.TransportOwner = DadTransportOwner.DadDirect;
            options.QueueAuthority = DadQueueAuthority.LocalOnly;
            options.DutyExpectedPartySize = 1;
        }
        if (string.IsNullOrWhiteSpace(options.MogtomeDutyPolicy))
            options.MogtomeDutyPolicy = DadMogtomeDutyPolicies.PresetHandoff;
        options.StopPolicy ??= new DadRunStopPolicy();
        options.StopPolicy.Normalize();
        options.IncludedAccountKeys = options.IncludedAccountKeys
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DadPlannerGroup? NormalizeSelectedGroup(DadPlannerGroup? selectedGroup)
    {
        if (selectedGroup == null || string.IsNullOrWhiteSpace(selectedGroup.GroupId))
            return null;

        selectedGroup.DisplayName = string.IsNullOrWhiteSpace(selectedGroup.DisplayName)
            ? "Dad Group"
            : selectedGroup.DisplayName.Trim();
        selectedGroup.MogtomeDutyPolicy = string.IsNullOrWhiteSpace(selectedGroup.MogtomeDutyPolicy)
            ? DadMogtomeDutyPolicies.PresetHandoff
            : selectedGroup.MogtomeDutyPolicy.Trim();
        selectedGroup.RunFamily = ResolveLaneDefinition(selectedGroup.ActivityMode).RunFamily;
        selectedGroup.StopPolicy ??= new DadRunStopPolicy();
        selectedGroup.StopPolicy.Normalize();
        selectedGroup.Slots = selectedGroup.Slots
            .Where(static slot => slot != null)
            .Select(static slot =>
            {
                slot.SlotId = string.IsNullOrWhiteSpace(slot.SlotId) ? "Slot" : slot.SlotId.Trim();
                slot.LaunchProfileId = slot.LaunchProfileId?.Trim() ?? string.Empty;
                slot.CharacterLoadInstruction ??= new DadCharacterLoadInstruction();
                slot.CharacterLoadInstruction.Normalize();
                return slot;
            })
            .ToList();
        return selectedGroup;
    }

    private static List<DadAcquiredCharacter> BuildAvailableCharacters(
        DadCharacterPool pool,
        DadAcquiredCharacter? localCharacter,
        DadPresetPlannerOptions options,
        DadPlannerGroup? selectedGroup)
    {
        var availableCharacters = pool.Characters
            .Where(character => MatchesPlannerFilters(character, localCharacter, options))
            .Select(static character => character.Clone())
            .ToList();

        if (selectedGroup != null)
        {
            foreach (var character in pool.Characters)
            {
                if (availableCharacters.Any(existing =>
                        string.Equals(DadRosterIdentity.BuildKey(existing), DadRosterIdentity.BuildKey(character), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (selectedGroup.Slots.Any(slot => MatchesGroupSlot(character, slot)))
                    availableCharacters.Add(character.Clone());
            }
        }

        return availableCharacters
            .OrderByDescending(character => GetPlanningPriority(character, options))
            .ThenBy(static character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DadPlannerFilterStats BuildFilterStats(DadCharacterPool pool, DadAcquiredCharacter? localCharacter, DadPresetPlannerOptions options)
    {
        var stats = new DadPlannerFilterStats
        {
            TotalCandidates = pool.Characters.Count,
        };

        foreach (var character in pool.Characters)
        {
            if (character.Source == DadCharacterSource.PeerRuntime && !IsPeerEligibleForRemoteWork(character))
                stats.ExcludedByPeerEligibility++;

            if (HasLocalIsolationReason(character))
                stats.ExcludedByLocalOnlyIsolation++;

            if (options.ConnectedOnly && !IsConnectedForPlanning(character))
                stats.ExcludedByConnectedFilter++;

            if (!options.AllowStaleForPlanning && character.Freshness == DadSnapshotFreshness.Stale)
                stats.ExcludedByStaleFilter++;

            if (!MatchesPlannerAccountFilter(character, options))
                stats.ExcludedByAccountFilter++;

            if (options.SameDatacenterOnly && !IsSameDatacenter(localCharacter, character))
                stats.ExcludedByDatacenterFilter++;
        }

        stats.CandidatesAfterFilters = pool.Characters.Count(character => MatchesPlannerFilters(character, localCharacter, options));
        return stats;
    }

    private static List<DadPresetCharacterSlot> BuildSlotAssignments(
        List<DadAcquiredCharacter> availableCharacters,
        DadPlannerLaneDefinition lane)
    {
        var remaining = availableCharacters
            .Select(static character => character.Clone())
            .ToList();
        var slotDefinitions = lane.RequiresRemoteParty
            ? PartySlotDefinitions
            : [new PlannerSlotDefinition("Runner", DadPartyRole.Any, true)];
        var selected = new List<DadPresetCharacterSlot>(slotDefinitions.Length);

        foreach (var slot in slotDefinitions)
        {
            var exactMatch = remaining.FirstOrDefault(candidate => IsExactRoleMatch(slot.RequiredRole, ClassifyRole(candidate)));
            var assignedCharacter = exactMatch;
            var isSubstitution = false;
            if (assignedCharacter == null && slot.AllowSubstitution)
            {
                assignedCharacter = remaining.FirstOrDefault();
                isSubstitution = assignedCharacter != null;
            }

            if (assignedCharacter != null)
                remaining.Remove(assignedCharacter);

            selected.Add(BuildSlot(slot, assignedCharacter, isSubstitution));
        }

        return selected;
    }

    private static List<DadPresetCharacterSlot> BuildGroupSlotAssignments(
        List<DadAcquiredCharacter> availableCharacters,
        DadPlannerGroup selectedGroup,
        DadPlannerLaneDefinition lane)
    {
        if (selectedGroup.Slots.Count == 0)
            return BuildSlotAssignments(availableCharacters, lane);

        var remaining = availableCharacters
            .Select(static character => character.Clone())
            .ToList();
        var selected = new List<DadPresetCharacterSlot>(selectedGroup.Slots.Count);

        foreach (var groupSlot in selectedGroup.Slots)
        {
            var candidates = remaining
                .Where(character => MatchesGroupSlot(character, groupSlot))
                .ToList();
            var assignedCharacter = candidates.FirstOrDefault(candidate =>
                                        !groupSlot.RequiredCharacterKey.IsEmpty &&
                                        string.Equals(candidate.CharacterKey, groupSlot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
                                    ?? candidates.FirstOrDefault(candidate => IsExactRoleMatch(groupSlot.RequiredRole, ClassifyRole(candidate)))
                                    ?? (groupSlot.AllowSubstitution ? candidates.FirstOrDefault() : null);
            var isSubstitution = assignedCharacter != null &&
                                 ((groupSlot.RequiredCharacterKey.IsEmpty &&
                                   !IsExactRoleMatch(groupSlot.RequiredRole, ClassifyRole(assignedCharacter))) ||
                                  (!groupSlot.RequiredCharacterKey.IsEmpty &&
                                   !string.Equals(assignedCharacter.CharacterKey, groupSlot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase)));

            if (assignedCharacter != null)
                remaining.RemoveAll(character =>
                    string.Equals(DadRosterIdentity.BuildKey(character), DadRosterIdentity.BuildKey(assignedCharacter), StringComparison.OrdinalIgnoreCase));

            selected.Add(BuildGroupSlot(groupSlot, assignedCharacter, isSubstitution));
        }

        return selected;
    }

    private static DadPresetCharacterSlot BuildGroupSlot(DadPlannerGroupSlot groupSlot, DadAcquiredCharacter? character, bool isSubstitution)
    {
        if (character == null)
        {
            var requirement = !groupSlot.RequiredCharacterKey.IsEmpty
                ? $"character {groupSlot.RequiredCharacterKey}"
                : !groupSlot.RequiredAccountKey.IsEmpty
                    ? $"account {groupSlot.RequiredAccountKey}"
                    : FormatRoleRequirement(groupSlot.RequiredRole);
            return new DadPresetCharacterSlot
            {
                SlotId = groupSlot.SlotId,
                RequiredRole = groupSlot.RequiredRole,
                RequiredAccountKey = groupSlot.RequiredAccountKey,
                RequiredCharacterKey = groupSlot.RequiredCharacterKey,
                AssignmentMode = groupSlot.RequiredCharacterKey.IsEmpty
                    ? DadSlotAssignmentMode.SpecificRole
                    : DadSlotAssignmentMode.SpecificCharacter,
                AllowSubstitution = groupSlot.AllowSubstitution,
                AssignmentSummary = "Missing group assignment",
                StatusText = "Missing",
                BlockerSummary = $"No candidate matched required {requirement}.",
            };
        }

        var readiness = FormatReadiness(character.Readiness);
        var freshness = FormatFreshness(character);
        var accountKey = groupSlot.RequiredAccountKey.IsEmpty
            ? GetPlannerAccountSelectionKey(character)
            : groupSlot.RequiredAccountKey;
        var requiredCharacterKey = groupSlot.RequiredCharacterKey.IsEmpty
            ? new DadCharacterKey(character.CharacterKey)
            : groupSlot.RequiredCharacterKey;
        var blockers = character.Blockers.Count == 0 ? "No blockers recorded." : string.Join(" | ", character.Blockers);
        if (character.Source == DadCharacterSource.XadbOnly)
            blockers = blockers == "No blockers recorded."
                ? "XADB-only/offline preview row; live runtime requires this account to connect."
                : $"{blockers} | XADB-only/offline preview row; live runtime requires this account to connect.";
        if (isSubstitution && !groupSlot.AllowSubstitution)
            blockers = $"{blockers} | Substitution is disabled for this group slot.";

        var assignmentSummary = isSubstitution
            ? $"Group substitution via {FormatSourceLabel(character.Source)}"
            : $"Group assignment via {FormatSourceLabel(character.Source)}";

        return new DadPresetCharacterSlot
        {
            SlotId = groupSlot.SlotId,
            RequiredRole = groupSlot.RequiredRole,
            RequiredAccountKey = accountKey,
            RequiredCharacterKey = requiredCharacterKey,
            AssignmentMode = DadSlotAssignmentMode.SpecificCharacter,
            AllowSubstitution = groupSlot.AllowSubstitution,
            ContentId = character.ContentId == 0 ? null : character.ContentId,
            CharacterKey = character.CharacterKey,
            IsSubstitution = isSubstitution,
            SelectedSource = character.Source,
            SelectedFreshness = character.Freshness,
            SelectedReadiness = character.Readiness,
            AssignmentSummary = assignmentSummary,
            StatusText = $"{(isSubstitution ? "substitution" : "exact")} | {readiness} | {freshness}",
            BlockerSummary = blockers,
        };
    }

    private static DadPresetCharacterSlot BuildSlot(PlannerSlotDefinition slot, DadAcquiredCharacter? character, bool isSubstitution)
    {
        if (character == null)
        {
            return new DadPresetCharacterSlot
            {
                SlotId = slot.SlotId,
                RequiredRole = slot.RequiredRole,
                RequiredAccountKey = new DadAccountKey(string.Empty),
                RequiredCharacterKey = new DadCharacterKey(string.Empty),
                AssignmentMode = DadSlotAssignmentMode.Auto,
                AllowSubstitution = slot.AllowSubstitution,
                IsSubstitution = false,
                AssignmentSummary = "Missing",
                StatusText = "Missing",
                BlockerSummary = $"No {FormatRoleRequirement(slot.RequiredRole)} candidate passed the current planner filters.",
            };
        }

        var readiness = FormatReadiness(character.Readiness);
        var freshness = FormatFreshness(character);
        var blockers = character.Blockers.Count == 0 ? "No blockers recorded." : string.Join(" | ", character.Blockers);
        var assignmentSummary = isSubstitution
            ? $"Substitution via {FormatSourceLabel(character.Source)}"
            : $"Exact assignment via {FormatSourceLabel(character.Source)}";

        return new DadPresetCharacterSlot
        {
            SlotId = slot.SlotId,
            RequiredRole = slot.RequiredRole,
            RequiredAccountKey = GetPlannerAccountSelectionKey(character),
            RequiredCharacterKey = new DadCharacterKey(character.CharacterKey),
            AssignmentMode = DadSlotAssignmentMode.SpecificCharacter,
            AllowSubstitution = slot.AllowSubstitution,
            ContentId = character.ContentId == 0 ? null : character.ContentId,
            CharacterKey = character.CharacterKey,
            IsSubstitution = isSubstitution,
            SelectedSource = character.Source,
            SelectedFreshness = character.Freshness,
            SelectedReadiness = character.Readiness,
            AssignmentSummary = assignmentSummary,
            StatusText = $"{(isSubstitution ? "substitution" : "exact")} | {readiness} | {freshness}",
            BlockerSummary = blockers,
        };
    }

    private static DadRunStopPolicy BuildResolvedStopPolicy(
        DadRunStopPolicy? source,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        var policy = (source ?? new DadRunStopPolicy()).Clone().Normalize();
        if (policy.Mode != DadPlannerStopMode.TargetLevel)
            return policy;

        var selectedKey = selectedSlots
            .Select(static slot => slot.CharacterKey)
            .FirstOrDefault(static key => !string.IsNullOrWhiteSpace(key)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedKey))
            return policy;

        policy.TargetCharacterKey = new DadCharacterKey(selectedKey);
        var character = availableCharacters.FirstOrDefault(candidate =>
            string.Equals(candidate.CharacterKey, selectedKey, StringComparison.OrdinalIgnoreCase));
        policy.TargetCharacterLabel = character == null
            ? selectedKey
            : FormatCharacterDisplay(character);
        return policy;
    }

    private static List<string> BuildStopPolicyBlockers(
        DadRunStopPolicy stopPolicy,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        stopPolicy.Normalize();
        if (stopPolicy.Mode != DadPlannerStopMode.TargetLevel)
            return [];

        var blockers = new List<string>();
        var targetKey = stopPolicy.TargetCharacterKey.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            blockers.Add("Target-level stop requires an exact selected character.");
            return blockers;
        }

        if (selectedSlots.All(slot => !string.Equals(slot.CharacterKey, targetKey, StringComparison.OrdinalIgnoreCase)))
            blockers.Add($"Target-level stop character '{targetKey}' is not selected in the planned roster.");

        var targetCharacter = availableCharacters.FirstOrDefault(character =>
            string.Equals(character.CharacterKey, targetKey, StringComparison.OrdinalIgnoreCase));
        if (targetCharacter == null)
        {
            blockers.Add($"Target-level stop character '{targetKey}' is not known in the planner pool.");
            return blockers;
        }

        if (!targetCharacter.CurrentLevel.HasValue)
        {
            blockers.Add($"Target-level stop character '{targetKey}' has no current level data.");
            return blockers;
        }

        if (targetCharacter.CurrentLevel.Value >= stopPolicy.TargetLevel)
            blockers.Add($"Target-level stop character '{targetKey}' is already level {targetCharacter.CurrentLevel.Value}/{stopPolicy.TargetLevel}.");

        return blockers;
    }

    private static string FormatCharacterDisplay(DadAcquiredCharacter character)
    {
        if (!string.IsNullOrWhiteSpace(character.CharacterName) && !string.IsNullOrWhiteSpace(character.WorldName))
            return $"{character.CharacterName}@{character.WorldName}";

        return string.IsNullOrWhiteSpace(character.CharacterKey)
            ? "(unknown)"
            : character.CharacterKey;
    }

    private string BuildPreviewScope(DadPresetPlannerOptions options, int localCandidateCount, int remoteCandidateCount, bool blocked)
    {
        if (options.OperatorMode != DadPlannerOperatorMode.TestOnThisMachine)
            return blocked ? "Full remote-party preview is blocked until the missing workers reconnect." : "Full remote-party preview is ready.";

        if (localCandidateCount <= 0)
            return "Preview-only: no local worker is ready on this machine yet.";

        return remoteCandidateCount <= 0
            ? "Preview-only: single-worker/local validation is possible here, but remote participants are still missing."
            : "Preview-only: local operator validation is possible here before the full remote retest.";
    }

    private DadReadinessState ResolveValidationState(DadPresetPlannerOptions options, bool blocked, int localCandidateCount)
    {
        if (!blocked)
            return DadReadinessState.Ready;

        if (options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine && localCandidateCount > 0)
            return DadReadinessState.Deferred;

        return DadReadinessState.Blocked;
    }

    private string BuildValidationSummary(
        DadActivityPreset preset,
        DadPresetPlannerOptions options,
        int missingRoleSlotCount,
        int localCandidateCount,
        int remoteCandidateCount)
    {
        if (preset.ValidationState == DadReadinessState.Ready)
            return "Ready for full typed roster planning.";

        if (options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine && localCandidateCount > 0)
        {
            return remoteCandidateCount <= 0
                ? $"Preview-only on this machine. {missingRoleSlotCount} remote slot(s) still missing."
                : "Preview-only on this machine. Full remote retest still recommended.";
        }

        return "Blocked. Review missing leader/role coverage and planner filter exclusions.";
    }

    private string BuildPlannerSummary(DadActivityPreset preset)
    {
        var leader = string.IsNullOrWhiteSpace(preset.LeaderCharacterKey)
            ? "leader missing"
            : $"leader {preset.LeaderCharacterKey}";
        var assignedSlots = preset.SelectedCharacters.Count(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey));
        var totalSlots = preset.SelectedCharacters.Count;
        var blockerText = preset.Blockers.Count == 0 ? "no blockers" : string.Join(" | ", preset.Blockers);
        return
            $"{preset.RunFamilyId} / {preset.ActivityModeId} | {preset.OperatorModeLabel} | {GetTransportOwnerLabel(preset.TransportOwner)} | {GetQueueAuthorityLabel(preset.QueueAuthority)} | {GetInviteAuthorityLabel(preset.InviteAuthority)} | stop {preset.StopPolicy.Describe()} | accounts {preset.AccountFilterSummary} | " +
            $"{leader} | slots {assignedSlots}/{Math.Max(1, totalSlots)} | {preset.FilterSummary} | {preset.ValidationSummary} | {blockerText}";
    }

    private static DadPlannerRunRequestPreview BlockRequest(DadPlannerRunRequestPreview result, string reason)
    {
        result.CanStart = false;
        result.BlockedReason = reason;
        result.StatusSummary = $"Planner request blocked: {reason}";
        if (!string.IsNullOrWhiteSpace(reason) &&
            result.ModuleBlockers.All(blocker => !string.Equals(blocker.Summary, reason, StringComparison.OrdinalIgnoreCase)))
        {
            result.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = result.ModuleId,
                Capability = "Planner",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = reason,
            });
        }

        return result;
    }

    private void PopulateRequestPreviewDetails(
        DadPlannerRunRequestPreview result,
        DadRunRequest request,
        DadPlannerLaneDefinition lane,
        DadPlannerDutyOption? selectedDuty)
    {
        result.RequestId = request.RequestId;
        result.ModuleId = request.Orchestration.ModuleTarget;
        result.StopPolicy = request.StopPolicy.Clone();
        result.QueueAuthority = request.Orchestration.QueueAuthority;
        result.ExpectedPartySize = request.Orchestration.RosterIntent.ExpectedPartySize;
        result.RequiredCharacterKeys = [..request.Orchestration.RequiredCharacterKeys];
        result.RequiredAccountKeys = [..request.Orchestration.RequiredAccountKeys];
        result.RequestJson = DadIpcJson.Serialize(request);
        result.ContractPreview = BuildContractPreview(result, request, lane, selectedDuty);
        result.ContractPreviewJson = DadIpcJson.Serialize(result.ContractPreview);
    }

    private DadPlannerRequestContractPreview BuildContractPreview(
        DadPlannerRunRequestPreview result,
        DadRunRequest request,
        DadPlannerLaneDefinition lane,
        DadPlannerDutyOption? selectedDuty)
        => new()
        {
            RequestId = request.RequestId,
            Lane = lane.DisplayName,
            ModuleId = request.Orchestration.ModuleTarget,
            TaskConfig = BuildContractTaskConfig(request, selectedDuty),
            StopPolicy = request.StopPolicy.Clone(),
            RequiredCharacterKeys = [..result.RequiredCharacterKeys],
            RequiredAccountKeys = [..result.RequiredAccountKeys],
            PartySize = request.Orchestration.RosterIntent.ExpectedPartySize,
            AuthorityMode = request.Orchestration.AuthorityMode,
            QueueAuthority = request.Orchestration.QueueAuthority,
            Startability = BuildStartabilityLabel(result, request),
            CanStart = result.CanStart,
            Blockers = BuildContractBlockers(result),
        };

    private object? BuildContractTaskConfig(DadRunRequest request, DadPlannerDutyOption? selectedDuty)
    {
        var dutyMetadata = BuildDutyMetadataPreview(selectedDuty);
        if (request.Msq != null)
        {
            return new
            {
                surfacedTask = nameof(DadMsqTask),
                request.Msq.Preset,
                legacyTask = nameof(DadDailyMsqTask),
                request.Msq.LegacyQueuePreset,
                request.Msq.Attempts,
                request.Msq.PreferTrustThenDutySupport,
            };
        }

        if (request.DailyMsq != null)
        {
            return new
            {
                surfacedLane = "MSQ",
                legacyTask = "DailyMsqPremade",
                request.DailyMsq.LanPartyPreset,
            };
        }

        if (request.DutySupport != null)
        {
            return new
            {
                request.DutySupport.ContentFinderConditionId,
                request.DutySupport.DutyName,
                execution = "DutySupportOnly",
                request.DutySupport.Attempts,
                dutyMetadata,
            };
        }

        if (request.Trust != null)
        {
            return new
            {
                request.Trust.ContentFinderConditionId,
                request.Trust.DutyName,
                execution = "TrustOnly",
                request.Trust.Attempts,
                dutyMetadata,
            };
        }

        if (request.PremadeDuty != null)
        {
            return new
            {
                request.PremadeDuty.ContentFinderConditionId,
                request.PremadeDuty.DutyName,
                syncMode = request.PremadeDuty.Unsynced ? "Unsynced" : "Synced",
                request.PremadeDuty.ExpectedPartySize,
                selectedDutyQueueSize = selectedDuty?.QueueSize ?? 0,
                queueLane = GetQueueAuthorityLabel(request.Orchestration.QueueAuthority),
                request.PremadeDuty.Attempts,
                dutyMetadata,
            };
        }

        if (request.Blunderville != null)
        {
            return new
            {
                request.Blunderville.Mode,
                emoteCommand = string.IsNullOrWhiteSpace(request.Blunderville.EmoteCommand)
                    ? "ConfiguredByCharacter"
                    : request.Blunderville.EmoteCommand,
                request.Blunderville.CompletionPolicy,
                request.Blunderville.Attempts,
            };
        }

        if (request.Mogtome != null)
        {
            return new
            {
                request.Mogtome.Preset,
                request.Mogtome.DutyPolicy,
                dutyPolicyLabel = GetMogtomeDutyPolicyLabel(request.Mogtome.DutyPolicy),
                queueLane = GetQueueAuthorityLabel(request.Orchestration.QueueAuthority),
                request.Mogtome.Attempts,
            };
        }

        if (request.Commendation != null)
        {
            return new
            {
                request.Commendation.Attempts,
                loopPolicy = "ShortDutyLoop",
                queueLane = GetQueueAuthorityLabel(request.Orchestration.QueueAuthority),
            };
        }

        if (request.Astrope != null)
        {
            return new
            {
                request.Astrope.Attempts,
                request.Astrope.ValidLocalTimeWindow,
                queueWindow = request.Astrope.ValidLocalTimeWindow.Describe(),
            };
        }

        if (request.CustomDuty != null)
        {
            return new
            {
                request.CustomDuty.ContentFinderConditionId,
                request.CustomDuty.DutyName,
                request.CustomDuty.Attempts,
                policy = "TypedCustomDuty",
                dutyMetadata,
            };
        }

        if (request.Dungeon != null)
        {
            return new
            {
                request.Dungeon.ContentFinderConditionId,
                dutyName = request.Dungeon.SelectedDungeon,
                syncMode = request.Dungeon.Unsynced ? "Unsynced" : "Synced",
                request.Dungeon.ExecutionPreference,
                request.Dungeon.Frequency,
                request.Dungeon.Count,
                dutyMetadata,
            };
        }

        return null;
    }

    private static object? BuildDutyMetadataPreview(DadPlannerDutyOption? selectedDuty)
        => selectedDuty == null
            ? null
            : new
            {
                selectedDuty.ShortCode,
                queueSize = selectedDuty.QueueSize,
                jobLevelRequired = selectedDuty.JobLevelRequired,
                jobLevelSync = selectedDuty.JobLevelSync,
                itemLevelRequired = selectedDuty.ItemLevelRequired,
                itemLevelSync = selectedDuty.ItemLevelSync,
                selectedDuty.FixedItemLevelSync,
                selectedDuty.AllowUndersized,
                selectedDuty.SupportsDutySupport,
                selectedDuty.SupportsTrust,
                selectedDuty.IsHighEndDuty,
            };

    private static string BuildStartabilityLabel(DadPlannerRunRequestPreview result, DadRunRequest request)
        => result.CanStart
            ? "Startable"
            : request.Orchestration.LocalOnlyOverride
                ? "PreviewOnly"
                : "Blocked";

    private static List<string> BuildContractBlockers(DadPlannerRunRequestPreview result)
    {
        var blockers = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.BlockedReason))
            blockers.Add(result.BlockedReason);

        blockers.AddRange(result.PlannerPreview.Blockers);
        blockers.AddRange(result.ModuleBlockers
            .Select(static blocker => blocker.Summary)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary)));
        return blockers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<DadPlannerDutyOption> GetPlannerDutyCatalog()
    {
        if (plannerDutyCatalog != null)
            return plannerDutyCatalog;

        plannerDutyCatalog = BuildPlannerDutyCatalog();
        return plannerDutyCatalog;
    }

    private IReadOnlyDictionary<uint, DadPlannerDutyOption> GetPlannerDutyCatalogById()
    {
        if (plannerDutyCatalogById != null)
            return plannerDutyCatalogById;

        plannerDutyCatalogById = GetPlannerDutyCatalog()
            .ToDictionary(static option => option.ContentFinderConditionId);
        return plannerDutyCatalogById;
    }

    private static string BuildDutySelectorBlocker(DadPlannerLaneDefinition lane, DadPlannerDutyOption? selectedDuty)
    {
        if (!lane.RequiresDutySelector)
            return string.Empty;

        if (selectedDuty == null)
            return $"{lane.DisplayName} requires a typed Duty Finder selection.";

        if (MatchesPlannerLaneDuty(selectedDuty, lane.ActivityMode))
            return string.Empty;

        return lane.ActivityMode switch
        {
            DadPlannerActivityMode.DutySupport => $"{selectedDuty.DutyDisplayName} #{selectedDuty.ContentFinderConditionId} is not marked as Duty Support content.",
            DadPlannerActivityMode.Trust => $"{selectedDuty.DutyDisplayName} #{selectedDuty.ContentFinderConditionId} is not marked as Trust content.",
            _ => $"{selectedDuty.DutyDisplayName} #{selectedDuty.ContentFinderConditionId} is not valid for {lane.DisplayName}.",
        };
    }

    private static int ResolveRequestedPartySize(
        DadPresetPlannerOptions options,
        DadPlannerDutyOption? selectedDuty,
        DadPlannerLaneDefinition lane)
        => lane.ActivityMode switch
        {
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade
                => Math.Max(2, options.DutyExpectedPartySize > 0
                    ? options.DutyExpectedPartySize
                    : selectedDuty?.QueueSize ?? lane.ExpectedPartySize),
            _ when lane.RequiresRemoteParty => Math.Max(1, lane.ExpectedPartySize),
            _ => 1,
        };

    private static bool MatchesPlannerLaneDuty(DadPlannerDutyOption option, DadPlannerActivityMode activityMode)
        => activityMode switch
        {
            DadPlannerActivityMode.DutySupport => option.SupportsDutySupport,
            DadPlannerActivityMode.Trust => option.SupportsTrust,
            _ => true,
        };

    private static bool SupportsTrust(ContentFinderCondition condition, IReadOnlyList<DawnContent> trustDawnRows)
    {
        if (condition.TerritoryType.ValueNullable?.ExVersion.ValueNullable == null)
            return false;

        var trustOrdinal = trustDawnRows
            .Select((row, index) => new { row, index })
            .FirstOrDefault(item => item.row.Content.ValueNullable?.RowId == condition.RowId)
            ?.index ?? -1;

        return TryGetTrustIndex(
            trustOrdinal,
            condition.TerritoryType.Value.ExVersion.Value.RowId,
            out _);
    }

    private static bool TryGetTrustIndex(int ordinal, uint exVersion, out int trustIndex)
    {
        trustIndex = ordinal switch
        {
            < 0 => -1,
            _ => exVersion switch
            {
                3 => ordinal,
                4 => ordinal - 11,
                5 => ordinal - 22,
                _ => -1,
            },
        };
        return trustIndex >= 0;
    }

    private static string BuildDutyMetadataSummary(
        string shortCode,
        int queueSize,
        int jobLevelRequired,
        int jobLevelSync,
        int itemLevelRequired,
        int itemLevelSync,
        bool fixedItemLevelSync,
        bool allowUndersized,
        bool supportsDutySupport,
        bool supportsTrust,
        bool isHighEndDuty)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(shortCode))
            parts.Add(shortCode);

        parts.Add($"queue {queueSize}");

        if (jobLevelRequired > 0)
            parts.Add($"lvl {jobLevelRequired}");

        if (jobLevelSync > 0)
            parts.Add($"sync {jobLevelSync}");

        if (itemLevelRequired > 0)
            parts.Add($"ilvl {itemLevelRequired}");

        if (itemLevelSync > 0)
            parts.Add(fixedItemLevelSync ? $"fixed ilvl {itemLevelSync}" : $"ilvl sync {itemLevelSync}");

        if (allowUndersized)
            parts.Add("undersized");

        if (supportsDutySupport)
            parts.Add("duty support");

        if (supportsTrust)
            parts.Add("trust");

        if (isHighEndDuty)
            parts.Add("high-end");

        return string.Join(" | ", parts);
    }

    private IReadOnlyList<DadPlannerDutyOption> BuildPlannerDutyCatalog()
    {
        var contentFinderSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        var dawnContentSheet = Plugin.DataManager.GetExcelSheet<DawnContent>();
        var participableSheet = Plugin.DataManager.GetSubrowExcelSheet<DawnContentParticipable>();

        var dutySupportContentIds = dawnContentSheet
            .Where(static row => row.Content.RowId != 0)
            .Where(row => participableSheet.GetSubrowCount(row.RowId) > 1)
            .Select(static row => row.Content.RowId)
            .ToHashSet();

        var trustDawnRows = dawnContentSheet
            .Where(static row => row.RowId != 0 && row.Content.RowId != 0 && row.Unknown13)
            .ToList();

        return contentFinderSheet
            .Where(static condition => condition.RowId != 0
                                       && condition.IsInDutyFinder
                                       && !condition.PvP
                                       && condition.TerritoryType.ValueNullable != null)
            .Select(condition =>
            {
                var dutyName = condition.Name.ToString().Trim();
                if (string.IsNullOrWhiteSpace(dutyName))
                    return null;

                var shortCode = condition.ShortCode.ToString().Trim();
                var queueSize = condition.QueueMaxPlayers > 0
                    ? condition.QueueMaxPlayers
                    : condition.ContentMemberType.ValueNullable?.MembersPerParty ?? (byte)1;
                var queueSizeInt = Math.Max(1, (int)queueSize);
                var supportsDutySupport = dutySupportContentIds.Contains(condition.RowId);
                var supportsTrust = SupportsTrust(condition, trustDawnRows);
                return new DadPlannerDutyOption
                {
                    ContentFinderConditionId = condition.RowId,
                    DutyDisplayName = dutyName,
                    ShortCode = shortCode,
                    QueueSize = queueSizeInt,
                    JobLevelRequired = condition.ClassJobLevelRequired,
                    JobLevelSync = condition.ClassJobLevelSync,
                    ItemLevelRequired = condition.ItemLevelRequired,
                    ItemLevelSync = condition.ItemLevelSync,
                    FixedItemLevelSync = condition.FixedItemLevelSync,
                    AllowUndersized = condition.AllowUndersized,
                    SupportsDutySupport = supportsDutySupport,
                    SupportsTrust = supportsTrust,
                    IsHighEndDuty = condition.HighEndDuty,
                    SearchText = string.Join(" ", new[]
                    {
                        dutyName,
                        shortCode,
                        condition.RowId.ToString(),
                        queueSizeInt.ToString(),
                    }),
                    SelectionLabel = string.IsNullOrWhiteSpace(shortCode)
                        ? $"{dutyName} #{condition.RowId}"
                        : $"{dutyName} [{shortCode}] #{condition.RowId}",
                    MetadataSummary = BuildDutyMetadataSummary(
                        shortCode,
                        queueSizeInt,
                        condition.ClassJobLevelRequired,
                        condition.ClassJobLevelSync,
                        condition.ItemLevelRequired,
                        condition.ItemLevelSync,
                        condition.FixedItemLevelSync,
                        condition.AllowUndersized,
                        supportsDutySupport,
                        supportsTrust,
                        condition.HighEndDuty),
                };
            })
            .Where(static option => option != null)
            .Select(static option => option!)
            .OrderBy(static option => option.DutyDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.ContentFinderConditionId)
            .ToList();
    }

    private static DadPlannerLaneDefinition ResolveLaneDefinition(DadPlannerActivityMode activityMode)
    {
        if (activityMode == DadPlannerActivityMode.DailyMsqPremade)
            activityMode = DadPlannerActivityMode.Msq;
        if (activityMode == DadPlannerActivityMode.DutyPremade)
            activityMode = DadPlannerActivityMode.PremadeDuty;

        return PlannerLaneDefinitions.FirstOrDefault(lane => lane.ActivityMode == activityMode)
               ?? PlannerLaneDefinitions[0];
    }

    private static DadPlannerLaneDefinition CloneLaneDefinition(DadPlannerLaneDefinition lane)
        => new()
        {
            ActivityMode = lane.ActivityMode,
            RunFamily = lane.RunFamily,
            ModuleId = lane.ModuleId,
            DisplayName = lane.DisplayName,
            Summary = lane.Summary,
            Maturity = lane.Maturity,
            MaturityLabel = lane.MaturityLabel,
            AccentColorHex = lane.AccentColorHex,
            DefaultAuthorityMode = lane.DefaultAuthorityMode,
            DefaultTransportOwner = lane.DefaultTransportOwner,
            DefaultQueueAuthority = lane.DefaultQueueAuthority,
            ExpectedPartySize = lane.ExpectedPartySize,
            RequiresRemoteParty = lane.RequiresRemoteParty,
            RequiresDutySelector = lane.RequiresDutySelector,
            UsesExternalHelper = lane.UsesExternalHelper,
            NextAction = lane.NextAction,
        };

    private static string BuildPlannerBlockerSummary(DadActivityPreset plannerPreview)
        => plannerPreview.Blockers.Count == 0
            ? plannerPreview.ValidationSummary
            : string.Join(" | ", plannerPreview.Blockers);

    private static List<DadAcquiredCharacter> ResolveSelectedCharacters(DadActivityPreset plannerPreview)
        => plannerPreview.SelectedCharacters
            .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
            .Select(slot => plannerPreview.AvailableCharacters.FirstOrDefault(character =>
                MatchesSelectedSlot(character, slot)))
            .Where(static character => character != null)
            .Select(static character => character!.Clone())
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool MatchesSelectedSlot(DadAcquiredCharacter character, DadPresetCharacterSlot slot)
    {
        if (!string.Equals(character.CharacterKey, slot.CharacterKey, StringComparison.OrdinalIgnoreCase))
            return false;

        if (slot.ContentId.HasValue && character.ContentId != 0)
            return character.ContentId == slot.ContentId.Value;

        return slot.RequiredAccountKey.IsEmpty || MatchesPlannerAccountKey(character, slot.RequiredAccountKey.Value);
    }

    private DadOrchestrationIntent BuildPlannerOrchestration(
        DadPresetPlannerOptions options,
        DadActivityPreset plannerPreview,
        IReadOnlyList<DadAcquiredCharacter> selectedCharacters,
        bool previewOnly,
        DadPlannerDutyOption? selectedDuty,
        DadPlannerGroup? selectedGroup)
    {
        var lane = ResolveLaneDefinition(options.ActivityMode);
        var forceLocalNpc = IsLocalNpcLane(options.ActivityMode);
        var selectedCharacterKeys = selectedCharacters
            .Select(static character => new DadCharacterKey(character.CharacterKey))
            .Where(static key => !key.IsEmpty)
            .ToList();
        var selectedRosterCharacters = plannerPreview.SelectedCharacters
            .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
            .Select(static slot => new DadRosterCharacterRef
            {
                AccountKey = slot.RequiredAccountKey,
                CharacterKey = new DadCharacterKey(slot.CharacterKey),
                ContentId = slot.ContentId ?? 0,
            })
            .Where(static reference => !reference.IsEmpty)
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<DadAccountKey> requiredAccountKeys = selectedGroup == null
            ? [..options.IncludedAccountKeys]
            : selectedGroup.Slots
                .Select(static slot => slot.RequiredAccountKey)
                .Where(static key => !key.IsEmpty)
                .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        List<DadRosterCharacterRef> groupRequiredRosterCharacters = selectedGroup?.Slots
            .Select(static slot => new DadRosterCharacterRef
            {
                AccountKey = slot.RequiredAccountKey,
                CharacterKey = slot.RequiredCharacterKey,
            })
            .Where(static reference => !reference.IsEmpty)
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        List<DadCharacterKey> groupRequiredCharacterKeys = selectedGroup?.Slots
            .Select(static slot => slot.RequiredCharacterKey)
            .Where(static key => !key.IsEmpty)
            .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        List<DadCharacterKey> requiredCharacterKeys = groupRequiredCharacterKeys.Count > 0
            ? groupRequiredCharacterKeys
            : selectedCharacterKeys;
        var expectedPartySize = previewOnly || forceLocalNpc
            ? 1
            : ResolveRequestedPartySize(options, selectedDuty, lane);

        return new DadOrchestrationIntent
        {
            LocalOnlyOverride = previewOnly || forceLocalNpc,
            AuthorityMode = previewOnly || forceLocalNpc ? DadAuthorityMode.LocalOnly : lane.DefaultAuthorityMode,
            TransportMode = previewOnly || forceLocalNpc || !lane.RequiresRemoteParty ? DadTransportMode.LocalOnly : DadTransportMode.LocalhostHybrid,
            ModuleTarget = ResolvePlannerModuleIdForRequest(options.ActivityMode, lane),
            QueueAuthority = previewOnly || forceLocalNpc ? DadQueueAuthority.LocalOnly : options.QueueAuthority,
            PreferredLeaderCharacterKey = new DadCharacterKey(plannerPreview.LeaderCharacterKey),
            RequiredAccountKeys = requiredAccountKeys,
            PreferredRosterCharacters = selectedRosterCharacters,
            RequiredRosterCharacters = selectedGroup == null && previewOnly
                ? []
                : groupRequiredRosterCharacters.Count > 0
                    ? groupRequiredRosterCharacters
                    : selectedRosterCharacters,
            PreferredCharacterKeys = [..selectedCharacterKeys],
            RequiredCharacterKeys = selectedGroup == null && previewOnly ? [] : [..requiredCharacterKeys],
            RosterIntent = new DadRosterIntent
            {
                ExpectedPartySize = expectedPartySize,
                RequireRemoteParticipants = !previewOnly && !forceLocalNpc && expectedPartySize > 1,
                AllowStoredXadbFallback = false,
                RequireExactCharacters = selectedGroup != null || !previewOnly,
            },
            ExecutionConstraintSummary = BuildPlannerExecutionConstraint(options),
        };
    }

    private static string BuildPlannerExecutionConstraint(DadPresetPlannerOptions options)
        => $"{options.ActivityMode}/{options.OperatorMode}/{options.TransportOwner}/{options.QueueAuthority}";

    private static DadModuleId ResolvePlannerModuleIdForRequest(DadPlannerActivityMode activityMode, DadPlannerLaneDefinition lane)
        => activityMode switch
        {
            DadPlannerActivityMode.DailyMsqPremade => DadModuleId.DailyMsq,
            DadPlannerActivityMode.DutyPremade => DadModuleId.PremadeDuty,
            _ => lane.ModuleId,
        };

    private static bool IsLocalNpcLane(DadPlannerActivityMode activityMode)
        => activityMode is DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.Trust;

    private static bool MatchesPlannerFilters(DadAcquiredCharacter character, DadAcquiredCharacter? localCharacter, DadPresetPlannerOptions options)
    {
        if (character.Source == DadCharacterSource.PeerRuntime && !IsPeerEligibleForRemoteWork(character))
            return false;

        if (options.ConnectedOnly && !IsConnectedForPlanning(character))
            return false;

        if (!options.AllowStaleForPlanning && character.Freshness == DadSnapshotFreshness.Stale)
            return false;

        if (!MatchesPlannerAccountFilter(character, options))
            return false;

        return !options.SameDatacenterOnly || IsSameDatacenter(localCharacter, character);
    }

    private static bool MatchesGroupSlot(DadAcquiredCharacter character, DadPlannerGroupSlot slot)
    {
        if (!slot.RequiredCharacterKey.IsEmpty &&
            !string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!slot.RequiredAccountKey.IsEmpty && !MatchesPlannerAccountKey(character, slot.RequiredAccountKey.Value))
            return false;

        return true;
    }

    private static List<string> BuildPlannerGroupBlockers(DadPlannerGroup? selectedGroup, IReadOnlyList<DadPresetCharacterSlot> selectedSlots)
    {
        if (selectedGroup == null)
            return [];

        var blockers = new List<string>();
        foreach (var duplicateAccount in selectedGroup.Slots
                     .Select(static slot => slot.RequiredAccountKey.Value)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1)
                     .Select(static group => group.Key))
        {
            blockers.Add($"Planner group '{selectedGroup.DisplayName}' uses account '{duplicateAccount}' in multiple slots; one account can only satisfy one planned slot.");
        }

        foreach (var slot in selectedGroup.Slots.Where(static slot => slot.RequiredAccountKey.IsEmpty))
            blockers.Add($"Planner group slot '{slot.SlotId}' is missing a required account key.");

        foreach (var duplicateAssignedAccount in selectedSlots
                     .Select(static slot => slot.RequiredAccountKey.Value)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1)
                     .Select(static group => group.Key))
        {
            if (blockers.All(blocker => !blocker.Contains(duplicateAssignedAccount, StringComparison.OrdinalIgnoreCase)))
                blockers.Add($"Planner group selected account '{duplicateAssignedAccount}' for multiple slots; one account can only satisfy one planned slot.");
        }

        return blockers;
    }

    private static int GetPlanningPriority(DadAcquiredCharacter character, DadPresetPlannerOptions options)
    {
        var priority = 0;
        if (options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine && character.Source == DadCharacterSource.LocalRuntime)
            priority += 8;
        if (IsConnectedForPlanning(character))
            priority += 4;
        if (character.Readiness == DadReadinessState.Ready)
            priority += 2;
        if (character.Source == DadCharacterSource.LocalRuntime)
            priority += 1;
        return priority;
    }

    private static bool IsPeerEligibleForRemoteWork(DadAcquiredCharacter character)
        => character.Source == DadCharacterSource.PeerRuntime
           && character.Freshness is DadSnapshotFreshness.Live or DadSnapshotFreshness.Recent
           && character.Readiness is not DadReadinessState.Unavailable and not DadReadinessState.Stale
           && !HasLocalIsolationReason(character);

    private static bool IsConnectedForPlanning(DadAcquiredCharacter character)
        => character.Source == DadCharacterSource.PeerRuntime
            ? IsPeerEligibleForRemoteWork(character)
            : character.IsLiveConnected;

    private static bool HasLocalIsolationReason(DadAcquiredCharacter character)
        => character.Blockers.Any(IsLocalIsolationReason);

    private static bool IsLocalIsolationReason(string value)
        => value.Contains("dad is disabled", StringComparison.OrdinalIgnoreCase)
           || value.Contains("dad is in local-only mode", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameDatacenter(DadAcquiredCharacter? localCharacter, DadAcquiredCharacter candidate)
    {
        if (localCharacter == null)
            return true;

        if (localCharacter.DataCenterId.HasValue && candidate.DataCenterId.HasValue)
            return localCharacter.DataCenterId == candidate.DataCenterId;

        return string.IsNullOrWhiteSpace(localCharacter.DataCenterName)
               || string.IsNullOrWhiteSpace(candidate.DataCenterName)
               || string.Equals(localCharacter.DataCenterName, candidate.DataCenterName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExactRoleMatch(DadPartyRole requiredRole, DadPartyRole actualRole)
        => requiredRole switch
        {
            DadPartyRole.Any => true,
            DadPartyRole.Dps => actualRole is DadPartyRole.Melee or DadPartyRole.PhysicalRanged or DadPartyRole.Caster,
            _ => requiredRole == actualRole,
        };

    private static DadInviteAuthority ResolveEffectiveInviteAuthority(DadPresetPlannerOptions options)
        => options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine
           && options.InviteAuthority == DadInviteAuthority.PresetLeader
            ? DadInviteAuthority.NotNeeded
            : options.InviteAuthority;

    private string BuildAccountFilterSummary(DadCharacterPool pool, DadPresetPlannerOptions options)
    {
        if (options.IncludedAccountKeys.Count == 0)
            return "Any account";

        var knownLabelsByKey = GetPlannerAccountOptions()
            .GroupBy(static option => option.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().DisplayName,
                StringComparer.OrdinalIgnoreCase);
        var labels = options.IncludedAccountKeys
            .Select(key => knownLabelsByKey.TryGetValue(key.Value, out var label) ? label : key.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (labels.Count <= 2)
            return string.Join(", ", labels);

        return $"{labels.Count} selected ({string.Join(", ", labels.Take(2))}, +{labels.Count - 2})";
    }

    private static bool MatchesPlannerAccountFilter(DadAcquiredCharacter character, DadPresetPlannerOptions options)
    {
        if (options.IncludedAccountKeys.Count == 0)
            return true;

        return options.IncludedAccountKeys.Any(key => MatchesPlannerAccountKey(character, key.Value));
    }

    private static bool MatchesPlannerAccountKey(DadAcquiredCharacter character, string accountKey)
        => (!string.IsNullOrWhiteSpace(character.AccountId)
            && string.Equals(character.AccountId, accountKey, StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(character.AccountAlias)
               && string.Equals(character.AccountAlias, accountKey, StringComparison.OrdinalIgnoreCase));

    private static DadAccountKey GetPlannerAccountSelectionKey(DadAcquiredCharacter character)
        => !string.IsNullOrWhiteSpace(character.AccountId)
            ? new DadAccountKey(character.AccountId)
            : !string.IsNullOrWhiteSpace(character.AccountAlias)
                ? new DadAccountKey(character.AccountAlias)
                : new DadAccountKey(string.Empty);

    private static string BuildFilterSummary(DadPlannerFilterStats stats)
        => $"kept {stats.CandidatesAfterFilters}/{Math.Max(1, stats.TotalCandidates)} | connected -{stats.ExcludedByConnectedFilter} | stale -{stats.ExcludedByStaleFilter} | dc -{stats.ExcludedByDatacenterFilter} | accounts -{stats.ExcludedByAccountFilter} | local-only -{stats.ExcludedByLocalOnlyIsolation} | peer -{stats.ExcludedByPeerEligibility}";

    private static string FormatSourceLabel(DadCharacterSource source)
        => source switch
        {
            DadCharacterSource.LocalRuntime => "Local runtime",
            DadCharacterSource.PeerRuntime => "Peer runtime",
            DadCharacterSource.XadbOnly => "XADB only",
            DadCharacterSource.ManualUnresolved => "Manual unresolved",
            _ => source.ToString(),
        };

    private static string FormatRoleRequirement(DadPartyRole role)
        => role switch
        {
            DadPartyRole.Dps => "DPS",
            _ => role.ToString(),
        };

    private static string FormatReadiness(DadReadinessState readiness)
        => readiness switch
        {
            DadReadinessState.Ready => "ready",
            DadReadinessState.Deferred => "deferred",
            DadReadinessState.Blocked => "blocked",
            DadReadinessState.Unavailable => "unavailable",
            DadReadinessState.Stale => "stale",
            _ => "unknown",
        };

    private static string FormatFreshness(DadAcquiredCharacter character)
    {
        if (character.Source == DadCharacterSource.XadbOnly)
            return FormatRelativeAge(character.XadbSnapshotUtc);

        return character.Freshness switch
        {
            DadSnapshotFreshness.Live => "live",
            DadSnapshotFreshness.Recent => "recent",
            DadSnapshotFreshness.Stale => "stale",
            _ => "unknown",
        };
    }

    private static string FormatRelativeAge(DateTime? utc)
    {
        if (!utc.HasValue)
            return "unknown";

        var age = DateTime.UtcNow - utc.Value;
        if (age <= TimeSpan.FromMinutes(1))
            return "live";
        if (age < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))}m";
        if (age < TimeSpan.FromDays(1))
            return $"{Math.Max(1, (int)Math.Round(age.TotalHours))}h";

        return $"{Math.Max(1, (int)Math.Round(age.TotalDays))}d";
    }
}
