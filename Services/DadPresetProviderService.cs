using dad.Models;
using Lumina.Excel.Sheets;

namespace dad.Services;

public sealed class DadPresetProviderService
{
    private static readonly DadPlannerActivityMode[] PlannerActivityModes =
    [
        DadPlannerActivityMode.DutySupport,
        DadPlannerActivityMode.Trust,
        DadPlannerActivityMode.DutySupportLeveling,
        DadPlannerActivityMode.TrustLeveling,
        DadPlannerActivityMode.PremadeDuty,
        DadPlannerActivityMode.DutyPremade,
        DadPlannerActivityMode.DailyRoulette,
        DadPlannerActivityMode.Blunderville,
        DadPlannerActivityMode.Mogtome,
        DadPlannerActivityMode.Commendation,
        DadPlannerActivityMode.Astrope,
        DadPlannerActivityMode.LocalDuty,
        DadPlannerActivityMode.CustomDuty,
        DadPlannerActivityMode.Squadron,
        DadPlannerActivityMode.VariantVvd,
    ];

    private static readonly DadPlannerLaneDefinition[] PlannerLaneDefinitions =
    [
        new()
        {
            ActivityMode = DadPlannerActivityMode.Msq,
            RunFamily = DadPlannerRunFamily.Msq,
            ModuleId = DadModuleId.Msq,
            DisplayName = "MSQ Story Duty (NPC)",
            Summary = "Legacy MSQ Story configuration retained for compatibility; new selection and execution are unsupported.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Unsupported",
            AccentColorHex = "#EF4444",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select another activity explicitly. Daily Roulette -> Main Scenario remains supported and separate.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.DailyRoulette,
            RunFamily = DadPlannerRunFamily.DailyRoulette,
            ModuleId = DadModuleId.DailyMsq,
            DisplayName = "Daily Roulette",
            Summary = "Dad-owned synced full-party queue for an eligible four-player non-PvP roulette.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Guarded queue",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.LanParty,
            DefaultQueueAuthority = DadQueueAuthority.Leader,
            ExpectedPartySize = DadDailyRoulettePlannerRules.RequiredPartySize,
            RequiresRemoteParty = true,
            RequiresRouletteSelector = true,
            NextAction = "Select a Daily Roulette, then start the guarded synced four-Dad queue.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.DutySupport,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.DutySupport,
            DisplayName = "Duty Support",
            Summary = "Solo Duty Support native queue lane with selected Duty Finder content.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Live queue",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select Duty Support duty, then start guarded native Duty Support queue.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Trust,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.Trust,
            DisplayName = "Trust",
            Summary = "Solo Trust native queue lane with selected native Trust content.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Live queue",
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
            ActivityMode = DadPlannerActivityMode.DutySupportLeveling,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.DutySupport,
            DisplayName = "Duty Support Leveling",
            Summary = "Solo Duty Support auto-leveling lane; Dad selects the highest eligible Duty Support duty for the current job.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Auto-select",
            AccentColorHex = "#10B981",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            NextAction = "Start on the character/job to level; Dad selects the highest eligible Duty Support duty.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.TrustLeveling,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.Trust,
            DisplayName = "Trust Leveling",
            Summary = "Solo Trust auto-leveling lane; Dad selects the highest eligible Trust duty and refreshes NPC level data before party selection.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Auto-select",
            AccentColorHex = "#10B981",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            NextAction = "Start on the character/job to level; Dad selects Trust content and refreshes NPC levels.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Blunderville,
            RunFamily = DadPlannerRunFamily.Event,
            ModuleId = DadModuleId.Blunderville,
            DisplayName = "Blunderville",
            Summary = "Gold Saucer Blunderville planning lane; live executor is deferred.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Executor deferred",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            NextAction = "Blocked until guarded FGS callbacks are available.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Mogtome,
            RunFamily = DadPlannerRunFamily.FarmLoops,
            ModuleId = DadModuleId.Mogtome,
            DisplayName = "MOGTOME",
            Summary = "Dad-owned orchestration with narrow MOGTOME readiness/start/status/stop helper IPC.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Helper IPC live",
            AccentColorHex = "#A855F7",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.Leader,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            UsesExternalHelper = true,
            NextAction = "Manual-test leader/participant helper ownership, status, stop, and attempt limit.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Commendation,
            RunFamily = DadPlannerRunFamily.FarmLoops,
            ModuleId = DadModuleId.Commendation,
            DisplayName = "Commendation",
            Summary = "Typed Under the Armour premade duty loop; attempt stop is live and API15 target modes block without verified truth.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Attempt loop live",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.Leader,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            NextAction = "Use Attempts mode, or verify API15 adapter before total/gained targets.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Astrope,
            RunFamily = DadPlannerRunFamily.FarmLoops,
            ModuleId = DadModuleId.Astrope,
            DisplayName = "Astrope",
            Summary = "Timed Astrope farming planning lane; live executor is deferred.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Executor deferred",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.ServerDad,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.Leader,
            ExpectedPartySize = 4,
            RequiresRemoteParty = true,
            NextAction = "Blocked until Astrope time-window executor policy is enabled.",
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
            Summary = "Typed custom Duty Finder lane using local or premade execution by configured party size.",
            Maturity = DadLaneMaturity.LiveReady,
            MaturityLabel = "Typed queue live",
            AccentColorHex = "#3B82F6",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select CFC duty and party size, then start guarded local/premade queue.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.Squadron,
            RunFamily = DadPlannerRunFamily.LevelingNpc,
            ModuleId = DadModuleId.Squadron,
            DisplayName = "Squadron",
            Summary = "Command Squadron mission planner lane; guarded live callbacks remain blocked until in-game validation.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Guarded deferred",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select command mission duty; live start blocks until callback research is verified.",
        },
        new()
        {
            ActivityMode = DadPlannerActivityMode.VariantVvd,
            RunFamily = DadPlannerRunFamily.DutyFinder,
            ModuleId = DadModuleId.VariantVvd,
            DisplayName = "Variant / VVD",
            Summary = "Variant and Variant/VVD planner lane; guarded live callbacks remain blocked until ADS has solving coverage.",
            Maturity = DadLaneMaturity.IntegrationDeferred,
            MaturityLabel = "Guarded deferred",
            AccentColorHex = "#F59E0B",
            DefaultAuthorityMode = DadAuthorityMode.LocalOnly,
            DefaultTransportOwner = DadTransportOwner.DadDirect,
            DefaultQueueAuthority = DadQueueAuthority.LocalOnly,
            ExpectedPartySize = 1,
            RequiresDutySelector = true,
            NextAction = "Select Variant/VVD content; live start blocks until guarded queue callbacks are validated.",
        },
    ];

    private static readonly DadPlannerOperatorMode[] PlannerOperatorModes =
    [
        DadPlannerOperatorMode.RemotePartyPlan,
        DadPlannerOperatorMode.TestOnThisMachine,
    ];

    private static readonly PlannerSlotDefinition[] PartySlotDefinitions =
    [
        new(DadPlannerSlotRules.FormatSlotId(1), DadPartyRole.Tank),
        new(DadPlannerSlotRules.FormatSlotId(2), DadPartyRole.Healer),
        new(DadPlannerSlotRules.FormatSlotId(3), DadPartyRole.Dps),
        new(DadPlannerSlotRules.FormatSlotId(4), DadPartyRole.Dps),
    ];

    private readonly record struct PlannerSlotDefinition(string SlotId, DadPartyRole RequiredRole);

    private readonly DadModuleRegistry moduleRegistry;
    private readonly Func<IReadOnlyList<DadRosterAccountOption>> accountDirectoryProvider;
    private readonly Func<IReadOnlyList<DadAutoPartyRemoteBinding>> currentRemoteBindingsProvider;
    private IReadOnlyList<DadPlannerDutyOption>? plannerDutyCatalog;
    private IReadOnlyDictionary<uint, DadPlannerDutyOption>? plannerDutyCatalogById;
    private IReadOnlyList<DadPlannerRouletteOption>? plannerRouletteCatalog;

    public DadPresetProviderService(
        DadModuleRegistry moduleRegistry,
        Func<IReadOnlyList<DadRosterAccountOption>> accountDirectoryProvider,
        Func<IReadOnlyList<DadAutoPartyRemoteBinding>>? currentRemoteBindingsProvider = null)
    {
        this.moduleRegistry = moduleRegistry;
        this.accountDirectoryProvider = accountDirectoryProvider;
        this.currentRemoteBindingsProvider = currentRemoteBindingsProvider ?? (static () => []);
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
            DadPlannerRunFamily.LevelingNpc,
            DadPlannerRunFamily.DutyFinder,
            DadPlannerRunFamily.FarmLoops,
            DadPlannerRunFamily.Event,
            DadPlannerRunFamily.DailyRoulette,
        ];

    public IReadOnlyList<DadPlannerLaneDefinition> GetPlannerSubmodes(DadPlannerRunFamily runFamily)
        => PlannerLaneDefinitions
            .Where(lane => lane.RunFamily == runFamily && DadLegacyActivityRules.IsCreationActivity(lane.ActivityMode))
            .Select(CloneLaneDefinition)
            .ToArray();

    public DadPlannerLaneDefinition GetPlannerLaneDefinition(DadPlannerActivityMode activityMode)
        => CloneLaneDefinition(ResolveLaneDefinition(activityMode));

    public DadPlannerRunFamily GetPlannerRunFamily(DadPlannerActivityMode activityMode)
        => ResolveLaneDefinition(activityMode).RunFamily;

    public DadPlannerActivityMode GetDefaultPlannerSubmode(DadPlannerRunFamily runFamily)
        => PlannerLaneDefinitions.FirstOrDefault(lane =>
               lane.RunFamily == runFamily && DadLegacyActivityRules.IsCreationActivity(lane.ActivityMode))?.ActivityMode
           ?? DadPlannerActivityMode.DutySupport;

    public IReadOnlyList<DadPlannerRouletteOption> GetPlannerRouletteOptions()
        => GetPlannerRouletteCatalog().Select(static option => option.Clone()).ToList();

    public DadPlannerRouletteResolution ResolvePlannerRouletteTarget(DadQueueTarget? target)
        => DadDailyRoulettePlannerRules.ResolveTarget(target, GetPlannerRouletteCatalog());

    public DadPlannerRouletteResolution GetPlannerSelectedRoulette(DadPresetPlannerOptions options)
    {
        options.RouletteTarget ??= new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        var resolution = ResolvePlannerRouletteTarget(options.RouletteTarget);
        options.RouletteTarget = resolution.Target.Clone();
        return resolution;
    }

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

    public IReadOnlyList<DadPlannerDutyOption> GetPlannerDutyOptionsForTerritory(uint territoryType)
        => GetPlannerDutyCatalog()
            .Where(option => option.TerritoryType == territoryType)
            .OrderBy(static option => option.ContentFinderConditionId)
            .ToList();

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

    private DadPlannerDutyOption? ResolvePlannerSelectedDuty(
        DadPresetPlannerOptions options,
        DadCharacterPool pool,
        DadAcquiredCharacter? localCharacter,
        out string autoLevelBlocker)
    {
        autoLevelBlocker = string.Empty;
        if (options.ActivityMode is not DadPlannerActivityMode.DutySupportLeveling
            and not DadPlannerActivityMode.TrustLeveling)
        {
            return GetPlannerSelectedDuty(options);
        }

        var lane = options.ActivityMode == DadPlannerActivityMode.TrustLeveling
            ? DadNpcAutoLevelLane.Trust
            : DadNpcAutoLevelLane.DutySupport;
        return DadNpcAutoLevelSelector.SelectHighestEligibleDuty(
            GetPlannerDutyCatalog(),
            localCharacter ?? pool.Characters.FirstOrDefault(static character => character.Source == DadCharacterSource.LocalRuntime),
            lane,
            out autoLevelBlocker);
    }

    public DadPlannerDutyOption? GetPlannerDuty(uint contentFinderConditionId)
        => contentFinderConditionId != 0 &&
           GetPlannerDutyCatalogById().TryGetValue(contentFinderConditionId, out var duty)
            ? duty
            : null;

    public DadPlannerDutyOption? SelectHighestEligibleNpcDuty(
        DadAcquiredCharacter? character,
        DadNpcAutoLevelLane lane,
        out string blocker)
        => DadNpcAutoLevelSelector.SelectHighestEligibleDuty(
            GetPlannerDutyCatalog(),
            character,
            lane,
            out blocker);

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
        selectedGroup = NormalizeSelectedGroup(selectedGroup);
        var formationOnly = selectedGroup?.AutoPartyFormationOnly == true;
        var lane = ResolveEffectiveLaneDefinition(options.ActivityMode, selectedGroup);
        var localCharacter = pool.Characters.FirstOrDefault(static candidate => candidate.Source == DadCharacterSource.LocalRuntime);
        var selectedDuty = ResolvePlannerSelectedDuty(options, pool, localCharacter, out var autoLevelBlocker);
        var rouletteResolution = lane.RequiresRouletteSelector
            ? GetPlannerSelectedRoulette(options)
            : null;
        var requestedPartySize = ResolveRequestedPartySize(options, selectedDuty, lane, selectedGroup);
        var effectiveSelectedGroup = BuildEffectiveSelectedGroupForLane(
            options.ActivityMode,
            selectedGroup,
            requestedPartySize);
        var dutySelectorBlocker = string.IsNullOrWhiteSpace(autoLevelBlocker)
            ? BuildDutySelectorBlocker(lane, selectedDuty)
            : autoLevelBlocker;
        var rouletteSelectorBlocker = rouletteResolution?.Blocker ?? string.Empty;
        var effectiveInviteAuthority = ResolveEffectiveInviteAuthority(options);
        var filterStats = BuildFilterStats(pool, localCharacter, options);
        var accountFilterSummary = BuildAccountFilterSummary(pool, options);
        var availableCharacters = BuildAvailableCharacters(pool, localCharacter, options, effectiveSelectedGroup);

        var selectedCharacters = effectiveSelectedGroup?.Slots.Count > 0
            ? BuildGroupSlotAssignments(availableCharacters, effectiveSelectedGroup, lane)
            : BuildSlotAssignments(availableCharacters, lane, requestedPartySize);
        var stopPolicy = BuildResolvedStopPolicy(options.StopPolicy, selectedCharacters, availableCharacters);
        var stopPolicyBlockers = BuildStopPolicyBlockers(stopPolicy, selectedCharacters, availableCharacters);
        var groupBlockers = BuildPlannerGroupBlockers(
            effectiveSelectedGroup,
            selectedCharacters,
            availableCharacters);
        var localNpcEligibilityBlockers = BuildLocalNpcEligibilityBlockers(
            options.ActivityMode,
            selectedDuty,
            selectedCharacters,
            availableCharacters);
        var leaderSlot = selectedCharacters.FirstOrDefault(static slot => DadPlannerSlotRules.IsLeaderSlot(slot.SlotId));
        var leaderCandidate = leaderSlot == null || string.IsNullOrWhiteSpace(leaderSlot.CharacterKey)
            ? null
            : availableCharacters.FirstOrDefault(character => MatchesSelectedSlot(character, leaderSlot) && IsConnectedForPlanning(character));
        var plannedLeaderCharacterKey = leaderCandidate?.CharacterKey
                                        ?? leaderSlot?.CharacterKey
                                        ?? string.Empty;
        var slot1Blockers = BuildSlot1LeaderBlockers(leaderSlot, availableCharacters);
        var missingRoleSlots = selectedCharacters
            .Where(static slot =>
                string.IsNullOrWhiteSpace(slot.CharacterKey) &&
                string.IsNullOrWhiteSpace(slot.SharedIdentityToken))
            .Select(static slot => slot.SlotId)
            .ToList();
        var missingDutySelector = !string.IsNullOrWhiteSpace(dutySelectorBlocker);
        var missingRouletteSelector = !string.IsNullOrWhiteSpace(rouletteSelectorBlocker);
        var insufficientPlannerPartyShell = lane.RequiresRemoteParty && requestedPartySize > selectedCharacters.Count;
        var wakeValidation = BuildWakePolicyValidation(
            effectiveSelectedGroup,
            selectedCharacters,
            availableCharacters);
        var staticBlockers = new List<string>();
        var readinessBlockers = new List<string>();
        var runOnlyBlockers = new List<string>();
        staticBlockers.AddRange(BuildSharedIdentityBlockers(selectedGroup));
        var legacyActivityBlocker = DadLegacyActivityRules.GetValidationBlocker(options.ActivityMode);
        if (!string.IsNullOrWhiteSpace(legacyActivityBlocker))
        {
            staticBlockers.Add(legacyActivityBlocker);
            runOnlyBlockers.Add(legacyActivityBlocker);
        }
        if (leaderCandidate == null && slot1Blockers.Count == 0)
            readinessBlockers.Add("Slot1 leader/inviter is not connected and ready.");
        if (missingRoleSlots.Count > 0)
            staticBlockers.Add($"Missing role slots: {string.Join(", ", missingRoleSlots)}.");
        if (missingDutySelector)
        {
            staticBlockers.Add(dutySelectorBlocker);
            runOnlyBlockers.Add(dutySelectorBlocker);
        }
        if (missingRouletteSelector)
        {
            staticBlockers.Add(rouletteSelectorBlocker);
            runOnlyBlockers.Add(rouletteSelectorBlocker);
        }
        if (insufficientPlannerPartyShell)
        {
            var blocker = $"Selected duty needs party size {requestedPartySize}, but planner shell currently exposes only {selectedCharacters.Count} typed slot(s).";
            staticBlockers.Add(blocker);
            runOnlyBlockers.Add(blocker);
        }
        staticBlockers.AddRange(stopPolicyBlockers);
        runOnlyBlockers.AddRange(stopPolicyBlockers);
        staticBlockers.AddRange(groupBlockers);
        staticBlockers.AddRange(slot1Blockers.Where(static blocker => !IsLiveReadinessBlocker(blocker)));
        staticBlockers.AddRange(localNpcEligibilityBlockers);
        runOnlyBlockers.AddRange(localNpcEligibilityBlockers);
        staticBlockers.AddRange(wakeValidation.StaticBlockers);
        readinessBlockers.AddRange(slot1Blockers.Where(IsLiveReadinessBlocker));
        readinessBlockers.AddRange(wakeValidation.ReadinessBlockers);
        var validation = DadCrewToolsRules.EvaluateFormationAdmission(
            formationOnly,
            staticBlockers,
            readinessBlockers,
            wakeValidation.ScheduleBlockers,
            runOnlyBlockers);
        var blocked = !validation.CanStart;
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
            TransportOwner = lane.DefaultTransportOwner,
            InviteAuthority = effectiveInviteAuthority,
            QueueAuthority = lane.DefaultQueueAuthority,
            LaneDefinition = CloneLaneDefinition(lane),
            RosterSource = options.ConnectedOnly
                ? DadRosterSourceMode.ConnectedOnly
                : DadRosterSourceMode.ConnectedAndXadb,
            AvailableCharacters = availableCharacters,
            SelectedCharacters = selectedCharacters,
            LeaderCharacterKey = plannedLeaderCharacterKey,
            LeaderStatusText = leaderCandidate == null
                ? string.IsNullOrWhiteSpace(plannedLeaderCharacterKey)
                    ? "Slot1 leader/inviter is unresolved."
                    : $"Slot1 {plannedLeaderCharacterKey} is selected but not live, ready, and post-AR ready."
                : $"Slot1 {leaderCandidate.CharacterKey} | {FormatReadiness(leaderCandidate.Readiness)} | {FormatFreshness(leaderCandidate)} | {GetCharacterSourceLabel(leaderCandidate.Source)}",
            PreviewOnly = options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine,
            PreviewScope = BuildPreviewScope(options, localCandidateCount, remoteCandidateCount, blocked),
            AccountFilterSummary = accountFilterSummary,
            FilterStats = filterStats,
            FilterSummary = BuildFilterSummary(filterStats),
            CanSchedule = validation.CanSchedule,
            ReadinessSummary = validation.ReadinessSummary,
            StaticBlockers = [..validation.StaticBlockers],
            ReadinessBlockers = [..validation.ReadinessBlockers],
            ScheduleBlockers = [..validation.ScheduleBlockers],
        };

        preset.Blockers.AddRange(validation.StaticBlockers);
        preset.Blockers.AddRange(validation.ReadinessBlockers);
        preset.Blockers.AddRange(validation.ScheduleBlockers);

        if (selectedGroup != null)
        {
            var savedPrimarySlotCount = DadPlannerSlotRules.CountPrimarySlots(selectedGroup.Slots);
            var effectivePrimarySlotCount = effectiveSelectedGroup == null
                ? 0
                : DadPlannerSlotRules.CountPrimarySlots(effectiveSelectedGroup.Slots);
            if (effectiveSelectedGroup != null && effectivePrimarySlotCount != savedPrimarySlotCount)
            {
                preset.Notes.Add($"Planner group selected: {selectedGroup.DisplayName} ({effectivePrimarySlotCount} effective slot(s) from {savedPrimarySlotCount} saved slot(s) for {lane.DisplayName}).");
            }
            else
            {
                preset.Notes.Add($"Planner group selected: {selectedGroup.DisplayName} ({savedPrimarySlotCount} slot(s)).");
            }

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

        if (rouletteResolution?.Option != null && lane.RequiresRouletteSelector)
        {
            var state = rouletteResolution.IsAvailable ? "Available" : "Unavailable";
            preset.Notes.Add($"Daily Roulette: {rouletteResolution.Option.DisplayName} #{rouletteResolution.Option.RouletteId} | {state}");
        }

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
        DadPlannerGroup? selectedGroup = null,
        DadCompletionActions? completionFallback = null)
    {
        options ??= new DadPresetPlannerOptions();
        NormalizePlannerOptions(options);
        selectedGroup = NormalizeSelectedGroup(selectedGroup);
        var formationOnly = selectedGroup?.AutoPartyFormationOnly == true;
        var lane = ResolveEffectiveLaneDefinition(options.ActivityMode, selectedGroup);
        var requestModuleId = ResolvePlannerModuleIdForRequest(options.ActivityMode, lane);
        var localCharacter = pool.Characters.FirstOrDefault(static candidate => candidate.Source == DadCharacterSource.LocalRuntime);
        var selectedDuty = ResolvePlannerSelectedDuty(options, pool, localCharacter, out var autoLevelBlocker);
        var dutySelectorBlocker = BuildDutySelectorBlocker(lane, selectedDuty);
        if (!string.IsNullOrWhiteSpace(autoLevelBlocker))
            dutySelectorBlocker = autoLevelBlocker;
        var rouletteResolution = lane.RequiresRouletteSelector
            ? GetPlannerSelectedRoulette(options)
            : null;
        var rouletteSelectorBlocker = rouletteResolution?.Blocker ?? string.Empty;
        var requestedPartySize = ResolveRequestedPartySize(options, selectedDuty, lane, selectedGroup);
        var effectiveSelectedGroup = BuildEffectiveSelectedGroupForLane(
            options.ActivityMode,
            selectedGroup,
            requestedPartySize);
        var capability = moduleRegistry.GetCapability(requestModuleId);
        var startCapabilityBlocker = capability.Blockers.FirstOrDefault(static blocker =>
            string.Equals(blocker.Capability, "CanStartQueue", StringComparison.OrdinalIgnoreCase));

        var plannerPreview = plannerPreviewOverride ?? BuildPlannerPreview(pool, options, selectedGroup);
        var selectedCharacters = ResolveSelectedCharacters(plannerPreview);
        var previewOnly = options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine;
        var request = new DadRunRequest
        {
            RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
            RequestedAtUtc = requestedAtUtc ?? DateTime.UtcNow,
            RequestedBy = previewOnly ? "planner-preview" : selectedGroup == null ? "planner" : $"planner-group:{selectedGroup.DisplayName}",
            StopPolicy = plannerPreview.StopPolicy.Clone().Normalize(),
            CompletionActions = ResolvePlannerCompletionActions(options, effectiveSelectedGroup, completionFallback),
            Orchestration = BuildPlannerOrchestration(options, plannerPreview, selectedCharacters, previewOnly, effectiveSelectedGroup, lane, requestedPartySize),
        };

        PopulatePlannerRequestTask(
            request,
            options,
            selectedDuty,
            rouletteResolution?.Target,
            requestedPartySize,
            DadAdaptiveDutyProjectionRules.Resolve(options.ActivityMode, effectiveSelectedGroup).UsesPremadeExecutor);
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
                : lane.DefaultQueueAuthority,
            ExpectedPartySize = IsLocalNpcLane(options.ActivityMode)
                ? 1
                : options.OperatorMode == DadPlannerOperatorMode.TestOnThisMachine
                    ? 1
                    : requestedPartySize,
            ModuleBlockers = formationOnly
                ? []
                : capability.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            CanSchedule = plannerPreview.CanSchedule && (formationOnly || startCapabilityBlocker == null) && !previewOnly,
            ReadinessSummary = plannerPreview.ReadinessSummary,
            StaticBlockers = [..plannerPreview.StaticBlockers],
            ReadinessBlockers = [..plannerPreview.ReadinessBlockers],
            ScheduleBlockers = [..plannerPreview.ScheduleBlockers],
        };

        if (!formationOnly && !string.IsNullOrWhiteSpace(dutySelectorBlocker))
        {
            result.CanSchedule = false;
            AddValidationBlocker(result.StaticBlockers, dutySelectorBlocker);
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

        if (!formationOnly && !string.IsNullOrWhiteSpace(rouletteSelectorBlocker))
        {
            result.CanSchedule = false;
            AddValidationBlocker(result.StaticBlockers, rouletteSelectorBlocker);
            result.ModuleBlockers.Add(new DadModuleBlockerDto
            {
                ModuleId = requestModuleId,
                Capability = "RouletteSelector",
                Severity = DadModuleBlockerSeverity.Blocked,
                Summary = rouletteSelectorBlocker,
            });
            BlockRequest(result, rouletteSelectorBlocker);
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (plannerPreview.ValidationState == DadReadinessState.Blocked)
        {
            BlockRequest(result, BuildPlannerBlockerSummary(plannerPreview));
            if (result.CanSchedule)
                result.StatusSummary = $"Planner direct start is waiting on live readiness; scheduler takeover is allowed. {result.ReadinessSummary}";
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (selectedCharacters.Count == 0)
        {
            result.CanSchedule = false;
            AddValidationBlocker(result.StaticBlockers, "Planner request needs at least one selected typed character.");
            BlockRequest(result, "Planner request needs at least one selected typed character.");
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (previewOnly)
        {
            result.CanSchedule = false;
            result.CanStart = false;
            result.StatusSummary = "Preview-only request built. Local validation only; remote start remains disabled.";
            result.BlockedReason = plannerPreview.Blockers.Count == 0
                ? "Preview-only mode keeps remote start disabled."
                : BuildPlannerBlockerSummary(plannerPreview);
            PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
            return result;
        }

        if (!formationOnly && startCapabilityBlocker != null)
        {
            result.CanSchedule = false;
            AddValidationBlocker(result.StaticBlockers, startCapabilityBlocker.Summary);
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
        result.CanSchedule = true;
        result.StatusSummary = "Planner request ready to start.";
        PopulateRequestPreviewDetails(result, request, lane, selectedDuty);
        return result;
    }

    private static void PopulatePlannerRequestTask(
        DadRunRequest request,
        DadPresetPlannerOptions options,
        DadPlannerDutyOption? selectedDuty,
        DadQueueTarget? selectedRouletteTarget,
        int requestedPartySize,
        bool useAdaptivePremadeDuty)
    {
        switch (options.ActivityMode)
        {
            case DadPlannerActivityMode.Msq:
                request.Msq = new DadMsqTask
                {
                    Preset = "MSQ",
                    LegacyQueuePreset = "Daily MSQ",
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.DailyRoulette:
                request.DailyMsq = DadDailyRoulettePlannerRules.BuildWireCompatibleTask(
                    selectedRouletteTarget ?? options.RouletteTarget);
                break;
            case DadPlannerActivityMode.DutySupport:
            case DadPlannerActivityMode.DutySupportLeveling:
                request.DutySupport = new DadDutySupportTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                    AutoSelectHighestEligible = options.ActivityMode == DadPlannerActivityMode.DutySupportLeveling,
                };
                break;
            case DadPlannerActivityMode.Trust:
            case DadPlannerActivityMode.TrustLeveling:
                request.Trust = new DadTrustTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                    AutoSelectHighestEligible = options.ActivityMode == DadPlannerActivityMode.TrustLeveling,
                    RefreshNpcLevelsBeforeQueue = options.RefreshTrustNpcLevels,
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
                    QueueTarget = new DadQueueTarget
                    {
                        Kind = DadQueueTargetKind.DutyFinderDuty,
                        DisplayName = "Under the Armour",
                    },
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Astrope:
                request.Astrope = new DadAstropeTask
                {
                    QueueTarget = new DadQueueTarget
                    {
                        Kind = DadQueueTargetKind.Roulette,
                        Key = "Mentor",
                        DisplayName = "Mentor Roulette",
                    },
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.LocalDuty:
                DadAdaptiveDutyProjectionRules.PopulateDutyTask(
                    request,
                    new DadAdaptiveDutyProjection(
                        requestedPartySize,
                        requestedPartySize,
                        useAdaptivePremadeDuty),
                    selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    options.DutyUnsynced);
                break;
            case DadPlannerActivityMode.CustomDuty:
                request.CustomDuty = new DadCustomDutyTask
                {
                    QueueTarget = new DadQueueTarget
                    {
                        Kind = DadQueueTargetKind.DutyFinderDuty,
                        ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                        DisplayName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    },
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    ExpectedPartySize = Math.Max(1, requestedPartySize),
                    Unsynced = options.DutyUnsynced,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.Squadron:
                request.Squadron = new DadSquadronTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    Attempts = 1,
                };
                break;
            case DadPlannerActivityMode.VariantVvd:
                request.VariantVvd = new DadVariantVvdTask
                {
                    ContentFinderConditionId = selectedDuty?.ContentFinderConditionId ?? options.DutyContentFinderConditionId,
                    DutyName = selectedDuty?.DutyDisplayName ?? options.DutyDisplayName,
                    ExpectedPartySize = Math.Clamp(requestedPartySize, 1, 4),
                    Unsynced = options.DutyUnsynced,
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
            DadPlannerActivityMode.Msq => "MSQ Story Duty (NPC)",
            DadPlannerActivityMode.DailyRoulette => "Daily Roulette",
            DadPlannerActivityMode.DutySupport => "Duty Support",
            DadPlannerActivityMode.Trust => "Trust",
            DadPlannerActivityMode.DutySupportLeveling => "Duty Support Leveling",
            DadPlannerActivityMode.TrustLeveling => "Trust Leveling",
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => "Premade Duty",
            DadPlannerActivityMode.Blunderville => "Blunderville",
            DadPlannerActivityMode.Mogtome => "MOGTOME",
            DadPlannerActivityMode.Commendation => "Commendation",
            DadPlannerActivityMode.Astrope => "Astrope",
            DadPlannerActivityMode.LocalDuty => "Local Duty / Unsync",
            DadPlannerActivityMode.CustomDuty => "Custom Duty",
            DadPlannerActivityMode.Squadron => "Squadron",
            DadPlannerActivityMode.VariantVvd => "Variant / VVD",
            _ => activityMode.ToString(),
        };

    public string GetPlannerRunFamilyLabel(DadPlannerRunFamily runFamily)
        => runFamily switch
        {
            DadPlannerRunFamily.Msq => "MSQ Story",
            DadPlannerRunFamily.LevelingNpc => "Leveling / NPC",
            DadPlannerRunFamily.DutyFinder => "Duty Finder",
            DadPlannerRunFamily.FarmLoops => "Farm Loops",
            DadPlannerRunFamily.Event => "Event",
            DadPlannerRunFamily.DailyRoulette => "Daily Roulette",
            _ => runFamily.ToString(),
        };

    public string GetPlannerStopModeLabel(DadPlannerStopMode stopMode)
        => stopMode switch
        {
            DadPlannerStopMode.TargetLevel => "Target level",
            DadPlannerStopMode.ItemTarget => "Item target", // feature batch A
            DadPlannerStopMode.RestedXpDepleted => "Rested XP depleted",
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
            DadQueueAuthority.Leader => "Slot1 party leader",
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
            DadInviteAuthority.PresetLeader => "Slot1 inviter",
            DadInviteAuthority.ServerDad => "Slot1 inviter",
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
            DadPlannerActivityMode.Msq => "MSQ Story Duty (NPC)",
            DadPlannerActivityMode.DailyRoulette => "Daily Roulette",
            DadPlannerActivityMode.DutySupport => "Duty Support",
            DadPlannerActivityMode.Trust => "Trust",
            DadPlannerActivityMode.DutySupportLeveling => "Duty Support Leveling",
            DadPlannerActivityMode.TrustLeveling => "Trust Leveling",
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => "Premade Duty",
            DadPlannerActivityMode.Blunderville => "Blunderville",
            DadPlannerActivityMode.Mogtome => "MOGTOME",
            DadPlannerActivityMode.Commendation => "Commendation",
            DadPlannerActivityMode.Astrope => "Astrope",
            DadPlannerActivityMode.LocalDuty => "Local Duty / Unsync",
            DadPlannerActivityMode.CustomDuty => "Custom Duty",
            DadPlannerActivityMode.Squadron => "Squadron",
            DadPlannerActivityMode.VariantVvd => "Variant / VVD",
            _ => options.ActivityName,
        };
        options.PresetName = options.ActivityMode switch
        {
            DadPlannerActivityMode.Msq => "MSQ Story",
            DadPlannerActivityMode.DailyRoulette => "Daily Roulette Group",
            DadPlannerActivityMode.DutySupport => "Duty Support",
            DadPlannerActivityMode.Trust => "Trust",
            DadPlannerActivityMode.DutySupportLeveling => "Duty Support Leveling",
            DadPlannerActivityMode.TrustLeveling => "Trust Leveling",
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade => "Premade Duty Group",
            DadPlannerActivityMode.Blunderville => "Blunderville",
            DadPlannerActivityMode.Mogtome => "MOGTOME Group",
            DadPlannerActivityMode.Commendation => "Commendation Group",
            DadPlannerActivityMode.Astrope => "Astrope Group",
            DadPlannerActivityMode.LocalDuty => "Local Duty",
            DadPlannerActivityMode.CustomDuty => "Custom Duty",
            DadPlannerActivityMode.Squadron => "Squadron",
            DadPlannerActivityMode.VariantVvd => "Variant / VVD",
            _ => "Dad Planner",
        };
        var lane = ResolveLaneDefinition(options.ActivityMode);
        if (options.DutyExpectedPartySize <= 0 && options.DutyContentFinderConditionId == 0 && lane.ExpectedPartySize > 0)
            options.DutyExpectedPartySize = lane.ExpectedPartySize;
        options.InviteAuthority = DadInviteAuthority.PresetLeader;
        if (IsLocalNpcLane(options.ActivityMode))
        {
            options.TransportOwner = DadTransportOwner.DadDirect;
            options.QueueAuthority = DadQueueAuthority.LocalOnly;
            options.DutyExpectedPartySize = 1;
        }
        if (string.IsNullOrWhiteSpace(options.MogtomeDutyPolicy))
            options.MogtomeDutyPolicy = DadMogtomeDutyPolicies.PresetHandoff;
        options.RouletteTarget ??= new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        if (options.ActivityMode == DadPlannerActivityMode.DailyRoulette)
        {
            options.TransportOwner = DadTransportOwner.LanParty;
            options.QueueAuthority = DadQueueAuthority.Leader;
            options.DutyUnsynced = false;
            options.DutyExpectedPartySize = DadDailyRoulettePlannerRules.RequiredPartySize;
            options.RouletteTarget = ResolvePlannerRouletteTarget(options.RouletteTarget).Target;
        }
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

        var normalized = DadSchedulerGroupCloneRules.CloneWithSlots(
            selectedGroup,
            DadPlannerSlotRules.NormalizeGroupSlots(selectedGroup.Slots));
        normalized.DisplayName = string.IsNullOrWhiteSpace(normalized.DisplayName)
            ? "Dad Group"
            : normalized.DisplayName.Trim();
        normalized.MogtomeDutyPolicy = string.IsNullOrWhiteSpace(normalized.MogtomeDutyPolicy)
            ? DadMogtomeDutyPolicies.PresetHandoff
            : normalized.MogtomeDutyPolicy.Trim();
        normalized.RouletteTarget ??= new DadQueueTarget { Kind = DadQueueTargetKind.Roulette };
        normalized.RunFamily = ResolveLaneDefinition(normalized.ActivityMode).RunFamily;
        normalized.InviteAuthority = DadInviteAuthority.PresetLeader;
        if (normalized.ActivityMode == DadPlannerActivityMode.DailyRoulette)
        {
            normalized.TransportOwner = DadTransportOwner.LanParty;
            normalized.QueueAuthority = DadQueueAuthority.Leader;
            normalized.DutyUnsynced = false;
            normalized.DutyExpectedPartySize = DadDailyRoulettePlannerRules.RequiredPartySize;
        }
        normalized.StopPolicy ??= new DadRunStopPolicy();
        normalized.StopPolicy.Normalize();
        return normalized;
    }

    private static DadPlannerGroup? BuildEffectiveSelectedGroupForLane(
        DadPlannerActivityMode activityMode,
        DadPlannerGroup? selectedGroup,
        int requestedPartySize)
    {
        if (selectedGroup == null)
            return null;

        return DadEffectivePlannerGroupProjection.Project(
            selectedGroup,
            activityMode,
            requestedPartySize);
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
        DadPlannerLaneDefinition lane,
        int requestedPartySize)
    {
        var remaining = availableCharacters
            .Select(static character => character.Clone())
            .ToList();
        var slotDefinitions = BuildSlotDefinitions(lane, requestedPartySize);
        var selected = new List<DadPresetCharacterSlot>(slotDefinitions.Count);

        foreach (var slot in slotDefinitions)
        {
            var exactMatch = remaining.FirstOrDefault(candidate => IsExactRoleMatch(slot.RequiredRole, ClassifyRole(candidate)));
            var assignedCharacter = exactMatch;

            if (assignedCharacter != null)
                remaining.Remove(assignedCharacter);

            selected.Add(BuildSlot(slot, assignedCharacter));
        }

        return selected;
    }

    private static IReadOnlyList<PlannerSlotDefinition> BuildSlotDefinitions(
        DadPlannerLaneDefinition lane,
        int requestedPartySize)
    {
        if (!lane.RequiresRemoteParty)
            return [new PlannerSlotDefinition(DadPlannerSlotRules.LeaderSlotId, DadPartyRole.Any)];

        var slotCount = Math.Clamp(
            requestedPartySize <= 0 ? lane.ExpectedPartySize : requestedPartySize,
            DadPlannerSlotRules.MinSlotNumber,
            DadPlannerSlotRules.MaxSlotNumber);
        if (slotCount == PartySlotDefinitions.Length)
            return PartySlotDefinitions;

        var definitions = new List<PlannerSlotDefinition>(slotCount);
        for (var slotNumber = DadPlannerSlotRules.MinSlotNumber; slotNumber <= slotCount; slotNumber++)
        {
            definitions.Add(new PlannerSlotDefinition(
                DadPlannerSlotRules.FormatSlotId(slotNumber),
                ResolveDefaultSlotRole(slotNumber, slotCount)));
        }

        return definitions;
    }

    private static DadPartyRole ResolveDefaultSlotRole(int slotNumber, int slotCount)
        => slotCount >= 8
            ? slotNumber switch
            {
                1 or 2 => DadPartyRole.Tank,
                3 or 4 => DadPartyRole.Healer,
                _ => DadPartyRole.Dps,
            }
            : slotCount >= 4
                ? slotNumber switch
                {
                    1 => DadPartyRole.Tank,
                    2 => DadPartyRole.Healer,
                    _ => DadPartyRole.Dps,
                }
                : DadPartyRole.Any;

    private static List<DadPresetCharacterSlot> BuildGroupSlotAssignments(
        List<DadAcquiredCharacter> availableCharacters,
        DadPlannerGroup selectedGroup,
        DadPlannerLaneDefinition lane)
    {
        if (selectedGroup.Slots.Count == 0)
            return BuildSlotAssignments(availableCharacters, lane, lane.ExpectedPartySize);

        var remaining = availableCharacters
            .Select(static character => character.Clone())
            .ToList();
        var normalizedSlots = DadPlannerSlotRules.NormalizeGroupSlots(selectedGroup.Slots);
        var primaryRows = DadPlannerSlotRules.GetPrimaryRows(normalizedSlots);
        var selected = new List<DadPresetCharacterSlot>(primaryRows.Count);

        foreach (var primarySlot in primaryRows)
        {
            var resolvedSlot = primarySlot;
            DadAcquiredCharacter? assignedCharacter = null;
            foreach (var candidateSlot in DadPlannerSlotRules.GetRowsForSlot(normalizedSlots, primarySlot.SlotId))
            {
                if (TryGetRegisteredIslandIdentityToken(candidateSlot, out _))
                {
                    resolvedSlot = candidateSlot;
                    break;
                }

                assignedCharacter = SelectGroupSlotAssignment(remaining, candidateSlot);
                if (assignedCharacter == null)
                    continue;

                resolvedSlot = candidateSlot;
                break;
            }

            if (assignedCharacter != null)
                remaining.RemoveAll(character =>
                    string.Equals(DadRosterIdentity.BuildKey(character), DadRosterIdentity.BuildKey(assignedCharacter), StringComparison.OrdinalIgnoreCase));

            selected.Add(BuildGroupSlot(resolvedSlot, assignedCharacter, resolvedSlot.IsSubstitute));
        }

        return selected;
    }

    private static DadAcquiredCharacter? SelectGroupSlotAssignment(
        IReadOnlyList<DadAcquiredCharacter> remaining,
        DadPlannerGroupSlot groupSlot)
    {
        var candidates = remaining
            .Where(character => MatchesGroupSlot(character, groupSlot))
            .ToList();

        if (!groupSlot.RequiredCharacterKey.IsEmpty)
        {
            return candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.CharacterKey, groupSlot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase));
        }

        return groupSlot.RequiredRole == DadPartyRole.Any
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(candidate => IsExactRoleMatch(groupSlot.RequiredRole, ClassifyRole(candidate)));
    }

    private static DadPresetCharacterSlot BuildGroupSlot(DadPlannerGroupSlot groupSlot, DadAcquiredCharacter? character, bool isSubstitution)
    {
        if (TryGetRegisteredIslandIdentityToken(groupSlot, out var sharedIdentityToken))
        {
            return new DadPresetCharacterSlot
            {
                SlotId = groupSlot.SlotId,
                AllianceAssignment = groupSlot.AllianceAssignment,
                RequiredRole = groupSlot.RequiredRole,
                RequiredJobId = groupSlot.RequiredJobId,
                AdsLootMode = groupSlot.AdsLootMode,
                LevelSeekTarget = groupSlot.LevelSeekTarget,
                AssignmentMode = DadSlotAssignmentMode.SpecificCharacter,
                SharedIdentityToken = sharedIdentityToken,
                AllowSubstitution = false,
                IsSubstitution = isSubstitution,
                AssignmentSummary = "Registered-island assignment",
                StatusText = "Registered island",
            };
        }

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
                AllianceAssignment = groupSlot.AllianceAssignment,
                RequiredRole = groupSlot.RequiredRole,
                RequiredAccountKey = groupSlot.RequiredAccountKey,
                RequiredCharacterKey = groupSlot.RequiredCharacterKey,
                RequiredJobId = groupSlot.RequiredJobId,
                AdsLootMode = groupSlot.AdsLootMode,
                LevelSeekTarget = groupSlot.LevelSeekTarget,
                AssignmentMode = groupSlot.RequiredCharacterKey.IsEmpty
                    ? DadSlotAssignmentMode.SpecificRole
                    : DadSlotAssignmentMode.SpecificCharacter,
                AllowSubstitution = false,
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
        var assignmentSummary = isSubstitution
            ? $"Explicit substitute via {FormatSourceLabel(character.Source)}"
            : $"Group assignment via {FormatSourceLabel(character.Source)}";

        return new DadPresetCharacterSlot
        {
            SlotId = groupSlot.SlotId,
            AllianceAssignment = groupSlot.AllianceAssignment,
            RequiredRole = groupSlot.RequiredRole,
            RequiredAccountKey = accountKey,
            RequiredCharacterKey = requiredCharacterKey,
            RequiredJobId = groupSlot.RequiredJobId,
            AdsLootMode = groupSlot.AdsLootMode,
            LevelSeekTarget = groupSlot.LevelSeekTarget,
            AssignmentMode = DadSlotAssignmentMode.SpecificCharacter,
            AllowSubstitution = false,
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

    private static DadPresetCharacterSlot BuildSlot(PlannerSlotDefinition slot, DadAcquiredCharacter? character)
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
                AllowSubstitution = false,
                IsSubstitution = false,
                AssignmentSummary = "Missing",
                StatusText = "Missing",
                BlockerSummary = $"No {FormatRoleRequirement(slot.RequiredRole)} candidate passed the current planner filters.",
            };
        }

        var readiness = FormatReadiness(character.Readiness);
        var freshness = FormatFreshness(character);
        var blockers = character.Blockers.Count == 0 ? "No blockers recorded." : string.Join(" | ", character.Blockers);
        var assignmentSummary = $"Exact assignment via {FormatSourceLabel(character.Source)}";

        return new DadPresetCharacterSlot
        {
            SlotId = slot.SlotId,
            RequiredRole = slot.RequiredRole,
            RequiredAccountKey = GetPlannerAccountSelectionKey(character),
            RequiredCharacterKey = new DadCharacterKey(character.CharacterKey),
            AssignmentMode = DadSlotAssignmentMode.SpecificCharacter,
            AllowSubstitution = false,
            ContentId = character.ContentId == 0 ? null : character.ContentId,
            CharacterKey = character.CharacterKey,
            IsSubstitution = false,
            SelectedSource = character.Source,
            SelectedFreshness = character.Freshness,
            SelectedReadiness = character.Readiness,
            AssignmentSummary = assignmentSummary,
            StatusText = $"exact | {readiness} | {freshness}",
            BlockerSummary = blockers,
        };
    }

    private static DadRunStopPolicy BuildResolvedStopPolicy(
        DadRunStopPolicy? source,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
        => DadResolvedLevelTargetRules.ResolvePolicy(
            source,
            selectedSlots,
            availableCharacters);

    private static DadCompletionActions? ResolvePlannerCompletionActions(
        DadPresetPlannerOptions options,
        DadPlannerGroup? selectedGroup,
        DadCompletionActions? completionFallback)
        => (options.CompletionActions ?? selectedGroup?.CompletionActions ?? completionFallback)?.Clone();

    private static List<string> BuildStopPolicyBlockers(
        DadRunStopPolicy stopPolicy,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        stopPolicy.Normalize();
        if (stopPolicy.Mode != DadPlannerStopMode.TargetLevel)
            return [];

        if (stopPolicy.ResolvedLevelTargets.Count > 0)
        {
            var evaluation = DadResolvedLevelTargetRules.Evaluate(
                stopPolicy,
                new DadCharacterPool
                {
                    Characters = availableCharacters
                        .Select(static character => character.Clone())
                        .ToList(),
                });
            return evaluation.AllSatisfied
                ? [$"{evaluation.Summary} {string.Join(" ", evaluation.Evidence.Select(static evidence => evidence.Summary))}"]
                : [];
        }

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

        var currentLevel = DadRosterCharacterMerge.ResolveCurrentLevel(
            targetCharacter.JobLevels,
            targetCharacter.CurrentJobId,
            targetCharacter.CurrentLevel);
        if (!currentLevel.HasValue)
        {
            blockers.Add($"Target-level stop character '{targetKey}' has no current level data.");
            return blockers;
        }

        if (currentLevel.Value >= stopPolicy.TargetLevel)
            blockers.Add($"Target-level stop character '{targetKey}' is already level {currentLevel.Value}/{stopPolicy.TargetLevel}.");

        return blockers;
    }

    private static List<string> BuildLocalNpcEligibilityBlockers(
        DadPlannerActivityMode activityMode,
        DadPlannerDutyOption? selectedDuty,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        if (!IsLocalNpcLane(activityMode) || selectedDuty == null)
            return [];

        var selectedKey = selectedSlots
            .Select(static slot => slot.CharacterKey)
            .FirstOrDefault(static key => !string.IsNullOrWhiteSpace(key));
        if (string.IsNullOrWhiteSpace(selectedKey))
            return [];

        var character = availableCharacters.FirstOrDefault(candidate =>
            string.Equals(candidate.CharacterKey, selectedKey, StringComparison.OrdinalIgnoreCase));
        if (character == null)
            return [];

        var blocker = DadNpcDutyEligibility.GetBlocker(
            character,
            selectedDuty.DutyDisplayName,
            selectedDuty.ContentFinderConditionId,
            selectedDuty.JobLevelRequired);
        return string.IsNullOrWhiteSpace(blocker) ? [] : [blocker];
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
        {
            return preset.LaneDefinition.Maturity == DadLaneMaturity.LiveReady
                ? "Ready for full typed roster planning."
                : $"Planner roster checks pass; live lane remains deferred: {preset.LaneDefinition.NextAction}";
        }

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

    private static void AddValidationBlocker(List<string> blockers, string blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker) ||
            blockers.Any(existing => string.Equals(existing, blocker, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        blockers.Add(blocker.Trim());
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
        result.BlockedReason = result.CanStart
            ? string.Empty
            : DadPlannerValidationRules.BuildBlockedReason(result);
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
            CompletionActions = request.CompletionActions?.Clone(),
            RequiredCharacterKeys = [..result.RequiredCharacterKeys],
            RequiredAccountKeys = [..result.RequiredAccountKeys],
            PartySize = request.Orchestration.RosterIntent.ExpectedPartySize,
            AuthorityMode = request.Orchestration.AuthorityMode,
            QueueAuthority = request.Orchestration.QueueAuthority,
            Startability = BuildStartabilityLabel(result, request),
            CanStart = result.CanStart,
            CanSchedule = result.CanSchedule,
            ReadinessSummary = result.ReadinessSummary,
            StaticBlockers = [..result.StaticBlockers],
            ReadinessBlockers = [..result.ReadinessBlockers],
            ScheduleBlockers = [..result.ScheduleBlockers],
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
                surfacedLane = "Daily Roulette",
                legacyTask = nameof(DadDailyMsqTask),
                request.DailyMsq.LanPartyPreset,
                queueTarget = request.DailyMsq.QueueTarget.Clone(),
            };
        }

        if (request.DutySupport != null)
        {
            return new
            {
                request.DutySupport.ContentFinderConditionId,
                request.DutySupport.DutyName,
                execution = "DutySupportOnly",
                request.DutySupport.AutoSelectHighestEligible,
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
                request.Trust.AutoSelectHighestEligible,
                request.Trust.RefreshNpcLevelsBeforeQueue,
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

        if (request.Squadron != null)
        {
            return new
            {
                request.Squadron.ContentFinderConditionId,
                request.Squadron.DutyName,
                request.Squadron.Attempts,
                policy = "GuardedCommandMission",
                dutyMetadata,
            };
        }

        if (request.VariantVvd != null)
        {
            return new
            {
                request.VariantVvd.ContentFinderConditionId,
                request.VariantVvd.DutyName,
                syncMode = request.VariantVvd.Unsynced ? "Unsynced" : "Synced",
                request.VariantVvd.ExpectedPartySize,
                request.VariantVvd.Attempts,
                policy = "GuardedVariant",
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
            : result.CanSchedule
                ? "Schedulable"
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

    private IReadOnlyList<DadPlannerRouletteOption> GetPlannerRouletteCatalog()
    {
        if (plannerRouletteCatalog != null)
            return plannerRouletteCatalog;

        plannerRouletteCatalog = new DadRouletteCatalogService(Plugin.DataManager).GetOptions();
        return plannerRouletteCatalog;
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
            DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.DutySupportLeveling => $"{selectedDuty.DutyDisplayName} #{selectedDuty.ContentFinderConditionId} is not marked as Duty Support content.",
            DadPlannerActivityMode.Trust or DadPlannerActivityMode.TrustLeveling => $"{selectedDuty.DutyDisplayName} #{selectedDuty.ContentFinderConditionId} is not marked as Trust content.",
            _ => $"{selectedDuty.DutyDisplayName} #{selectedDuty.ContentFinderConditionId} is not valid for {lane.DisplayName}.",
        };
    }

    private static int ResolveRequestedPartySize(
        DadPresetPlannerOptions options,
        DadPlannerDutyOption? selectedDuty,
        DadPlannerLaneDefinition lane,
        DadPlannerGroup? selectedGroup = null)
        => selectedGroup?.AutoPartyFormationOnly == true
            ? DadPlannerSlotRules.CountPrimarySlots(selectedGroup.Slots)
            : lane.ActivityMode switch
        {
            DadPlannerActivityMode.LocalDuty
                => DadAdaptiveDutyProjectionRules.Resolve(lane.ActivityMode, selectedGroup).ExpectedPartySize,
            DadPlannerActivityMode.PremadeDuty or DadPlannerActivityMode.DutyPremade
                => Math.Max(2, options.DutyExpectedPartySize > 0
                    ? options.DutyExpectedPartySize
                    : selectedDuty?.QueueSize ?? lane.ExpectedPartySize),
            DadPlannerActivityMode.VariantVvd
                => Math.Clamp(options.DutyExpectedPartySize > 0
                    ? options.DutyExpectedPartySize
                    : selectedDuty?.QueueSize ?? lane.ExpectedPartySize, 1, 4),
            _ when lane.RequiresRemoteParty => Math.Max(1, lane.ExpectedPartySize),
            _ => 1,
        };

    private static bool MatchesPlannerLaneDuty(DadPlannerDutyOption option, DadPlannerActivityMode activityMode)
        => activityMode switch
        {
            DadPlannerActivityMode.DutySupport or DadPlannerActivityMode.DutySupportLeveling => option.SupportsDutySupport,
            DadPlannerActivityMode.Trust or DadPlannerActivityMode.TrustLeveling => option.SupportsTrust,
            DadPlannerActivityMode.Squadron => option.QueueSize == 1,
            DadPlannerActivityMode.VariantVvd => option.QueueSize <= 4,
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
                    TerritoryType = condition.TerritoryType.Value.RowId,
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
        if (activityMode == DadPlannerActivityMode.DutyPremade)
            activityMode = DadPlannerActivityMode.PremadeDuty;

        return PlannerLaneDefinitions.FirstOrDefault(lane => lane.ActivityMode == activityMode)
               ?? PlannerLaneDefinitions.First(lane => lane.ActivityMode == DadPlannerActivityMode.DutySupport);
    }

    private static DadPlannerLaneDefinition ResolveEffectiveLaneDefinition(
        DadPlannerActivityMode activityMode,
        DadPlannerGroup? selectedGroup)
    {
        var lane = CloneLaneDefinition(ResolveLaneDefinition(activityMode));
        var adaptive = DadAdaptiveDutyProjectionRules.Resolve(activityMode, selectedGroup);
        if (!adaptive.UsesPremadeExecutor)
            return lane;

        lane.ModuleId = DadModuleId.PremadeDuty;
        lane.Summary = $"Exact {adaptive.ExpectedPartySize}-character Dad party using the guarded premade Duty Finder executor.";
        lane.DefaultAuthorityMode = DadAuthorityMode.ServerDad;
        lane.DefaultTransportOwner = DadTransportOwner.LanParty;
        lane.DefaultQueueAuthority = DadQueueAuthority.Leader;
        lane.ExpectedPartySize = adaptive.ExpectedPartySize;
        lane.RequiresRemoteParty = true;
        return lane;
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
            RequiresRouletteSelector = lane.RequiresRouletteSelector,
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
        DadPlannerGroup? selectedGroup,
        DadPlannerLaneDefinition lane,
        int requestedPartySize)
    {
        var formationOnly = selectedGroup?.AutoPartyFormationOnly == true;
        var forceLocalNpc = !formationOnly && IsLocalNpcLane(options.ActivityMode);
        var selectedCharacterKeys = selectedCharacters
            .Select(static character => new DadCharacterKey(character.CharacterKey))
            .Where(static key => !key.IsEmpty)
            .ToList();
        var selectedRosterCharacters = BuildPlannerRosterCharacters(plannerPreview, selectedGroup);
        List<DadAccountKey> requiredAccountKeys = selectedGroup == null
            ? [..options.IncludedAccountKeys]
            : selectedRosterCharacters
                .Select(static reference => reference.AccountKey)
                .Where(static key => !key.IsEmpty)
                .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        List<DadRosterCharacterRef> groupRequiredRosterCharacters = selectedGroup == null
            ? []
            : selectedRosterCharacters
                .Where(static reference => !reference.IsEmpty)
                .ToList();
        List<DadCharacterKey> groupRequiredCharacterKeys = selectedGroup == null
            ? []
            : selectedRosterCharacters
                .Select(static reference => reference.CharacterKey)
                .Where(static key => !key.IsEmpty)
                .DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        List<DadCharacterKey> requiredCharacterKeys = groupRequiredCharacterKeys.Count > 0
            ? groupRequiredCharacterKeys
            : selectedCharacterKeys;
        var expectedPartySize = previewOnly || forceLocalNpc ? 1 : requestedPartySize;
        var inviteAuthority = previewOnly || forceLocalNpc || expectedPartySize <= 1
            ? DadInviteAuthority.NotNeeded
            : ResolveEffectiveInviteAuthority(options);

        return new DadOrchestrationIntent
        {
            LocalOnlyOverride = previewOnly || forceLocalNpc,
            AuthorityMode = previewOnly || forceLocalNpc
                ? DadAuthorityMode.LocalOnly
                : formationOnly
                    ? DadAuthorityMode.ServerDad
                    : lane.DefaultAuthorityMode,
            TransportMode = previewOnly || forceLocalNpc || (!formationOnly && !lane.RequiresRemoteParty)
                ? DadTransportMode.LocalOnly
                : DadTransportMode.ServerHub,
            ModuleTarget = formationOnly
                ? DadModuleId.None
                : ResolvePlannerModuleIdForRequest(options.ActivityMode, lane),
            QueueAuthority = previewOnly || forceLocalNpc
                ? DadQueueAuthority.LocalOnly
                : formationOnly
                    ? DadQueueAuthority.Leader
                    : lane.DefaultQueueAuthority,
            InviteAuthority = inviteAuthority,
            PreferredLeaderCharacterKey = new DadCharacterKey(plannerPreview.LeaderCharacterKey),
            PreferredInviterCharacterKey = inviteAuthority == DadInviteAuthority.PresetLeader
                ? new DadCharacterKey(plannerPreview.LeaderCharacterKey)
                : new DadCharacterKey(string.Empty),
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
            // Proposal ids are created at run admission and never copied from saved planner state.
            AutoPartyProposalId = string.Empty,
            AutoPartyFormationOnly = selectedGroup?.AutoPartyFormationOnly ?? false,
        };
    }

    private static List<DadRosterCharacterRef> BuildPlannerRosterCharacters(
        DadActivityPreset plannerPreview,
        DadPlannerGroup? selectedGroup)
    {
        if (selectedGroup == null)
        {
            return plannerPreview.SelectedCharacters
                .Where(static slot => !string.IsNullOrWhiteSpace(slot.CharacterKey))
                .Select(static slot => new DadRosterCharacterRef
                {
                    AccountKey = slot.RequiredAccountKey,
                    CharacterKey = new DadCharacterKey(slot.CharacterKey),
                    ContentId = slot.ContentId ?? 0,
                    RequiredJobId = slot.RequiredJobId,
                    AdsLootMode = slot.AdsLootMode,
                })
                .Where(static reference => !reference.IsEmpty)
                .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var normalizedRows = DadPlannerSlotRules.NormalizeGroupSlots(selectedGroup.Slots);
        var primaryRows = DadPlannerSlotRules.GetPrimaryRows(normalizedRows);
        var roster = new List<DadRosterCharacterRef>(primaryRows.Count);
        foreach (var primaryRow in primaryRows)
        {
            var selectedSlot = plannerPreview.SelectedCharacters.FirstOrDefault(slot =>
                string.Equals(
                    DadPlannerSlotRules.NormalizeStrictSlotId(slot.SlotId),
                    primaryRow.SlotId,
                    StringComparison.OrdinalIgnoreCase));
            if (TryGetRegisteredIslandIdentityToken(primaryRow, out var sharedIdentityToken) ||
                !string.IsNullOrWhiteSpace(selectedSlot?.SharedIdentityToken))
            {
                roster.Add(new DadRosterCharacterRef
                {
                    RequiredJobId = selectedSlot?.RequiredJobId ?? primaryRow.RequiredJobId,
                    SharedIdentityToken = string.IsNullOrWhiteSpace(sharedIdentityToken)
                        ? selectedSlot!.SharedIdentityToken.Trim()
                        : sharedIdentityToken,
                });
                continue;
            }

            if (selectedSlot == null || string.IsNullOrWhiteSpace(selectedSlot.CharacterKey))
                continue;

            roster.Add(new DadRosterCharacterRef
            {
                AccountKey = selectedSlot.RequiredAccountKey,
                CharacterKey = new DadCharacterKey(selectedSlot.CharacterKey),
                ContentId = selectedSlot.ContentId ?? 0,
                RequiredJobId = selectedSlot.RequiredJobId,
                AdsLootMode = selectedSlot.AdsLootMode,
            });
        }

        return roster;
    }

    private static string BuildPlannerExecutionConstraint(DadPresetPlannerOptions options)
        => $"{options.ActivityMode}/{options.OperatorMode}/{options.TransportOwner}/{options.QueueAuthority}";

    private static DadModuleId ResolvePlannerModuleIdForRequest(DadPlannerActivityMode activityMode, DadPlannerLaneDefinition lane)
        => activityMode switch
        {
            DadPlannerActivityMode.DailyRoulette => DadModuleId.DailyMsq,
            DadPlannerActivityMode.DutyPremade => DadModuleId.PremadeDuty,
            DadPlannerActivityMode.DutySupportLeveling => DadModuleId.DutySupport,
            DadPlannerActivityMode.TrustLeveling => DadModuleId.Trust,
            _ => lane.ModuleId,
        };

    private static bool IsLocalNpcLane(DadPlannerActivityMode activityMode)
        => activityMode is DadPlannerActivityMode.DutySupport
            or DadPlannerActivityMode.Trust
            or DadPlannerActivityMode.DutySupportLeveling
            or DadPlannerActivityMode.TrustLeveling
            or DadPlannerActivityMode.Squadron;

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
        if (TryGetRegisteredIslandIdentityToken(slot, out _))
            return false;

        if (!slot.RequiredCharacterKey.IsEmpty &&
            !string.Equals(character.CharacterKey, slot.RequiredCharacterKey.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!slot.RequiredAccountKey.IsEmpty && !MatchesPlannerAccountKey(character, slot.RequiredAccountKey.Value))
            return false;

        return true;
    }

    private static bool TryGetRegisteredIslandIdentityToken(
        DadPlannerGroupSlot slot,
        out string identityToken)
    {
        identityToken = slot.SharedIdentity?.IdentityToken?.Trim() ?? string.Empty;
        return identityToken.Length > 0;
    }

    private static List<string> BuildSlot1LeaderBlockers(
        DadPresetCharacterSlot? leaderSlot,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        if (leaderSlot == null)
            return ["Slot1 is required as the preset leader and inviter."];

        var blockers = new List<string>();
        if (string.IsNullOrWhiteSpace(leaderSlot.CharacterKey))
        {
            blockers.Add("Slot1 leader/inviter has no resolved character.");
            return blockers;
        }

        var leader = availableCharacters.FirstOrDefault(character => MatchesSelectedSlot(character, leaderSlot));
        if (leader == null)
        {
            blockers.Add($"Slot1 leader/inviter character '{leaderSlot.CharacterKey}' is not known to the planner.");
            return blockers;
        }

        if (!IsConnectedForPlanning(leader))
        {
            var liveSameAccount = leaderSlot.RequiredAccountKey.IsEmpty
                ? null
                : availableCharacters.FirstOrDefault(character =>
                    !string.Equals(character.CharacterKey, leader.CharacterKey, StringComparison.OrdinalIgnoreCase) &&
                    MatchesPlannerAccountKey(character, leaderSlot.RequiredAccountKey.Value) &&
                    IsConnectedForPlanning(character));
            blockers.Add(liveSameAccount == null
                ? $"Slot1 leader/inviter '{leader.CharacterKey}' is not live, ready, and post-AR ready."
                : $"Slot1 leader/inviter account is live as '{liveSameAccount.CharacterKey}', not required character '{leader.CharacterKey}'.");
        }

        var partyBlockers = leader.Blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (partyBlockers.Count > 0)
            blockers.Add($"Slot1 leader/inviter '{leader.CharacterKey}' is not valid for the party: {string.Join(" | ", partyBlockers)}");

        return blockers;
    }

    private static WakePolicyValidation BuildWakePolicyValidation(
        DadPlannerGroup? selectedGroup,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        var result = new WakePolicyValidation();
        foreach (var selectedSlot in selectedSlots)
        {
            if (!string.IsNullOrWhiteSpace(selectedSlot.SharedIdentityToken))
                continue;

            var wakePolicy = selectedGroup == null
                ? DadSchedulerWakePolicy.LaunchIfOffline
                : ResolveSelectedWakePolicy(selectedGroup, selectedSlot);
            if (wakePolicy == DadSchedulerWakePolicy.LoadCharacterIfOnline)
            {
                result.StaticBlockers.Add(
                    $"Slot {selectedSlot.SlotId}: {DadWakePolicyRules.LoadCharacterStubReason}");
            }

            if (string.IsNullOrWhiteSpace(selectedSlot.CharacterKey))
                continue;

            var character = availableCharacters.FirstOrDefault(candidate => MatchesSelectedSlot(candidate, selectedSlot));
            var connected = character != null && IsConnectedForPlanning(character);
            if (!connected)
            {
                result.ReadinessBlockers.Add(
                    $"Slot {selectedSlot.SlotId} character '{selectedSlot.CharacterKey}' is not live, ready, and post-AR ready.");
            }

            if (selectedGroup == null)
            {
                if (!connected)
                {
                    result.ScheduleBlockers.Add(
                        $"Slot {selectedSlot.SlotId} has no persisted wake policy, so the scheduler cannot resolve '{selectedSlot.CharacterKey}'.");
                }
                continue;
            }

            if (wakePolicy == DadSchedulerWakePolicy.AlreadyOnlineOnly && !connected)
            {
                result.ScheduleBlockers.Add(
                    $"Slot {selectedSlot.SlotId} uses Already online and cannot wake or relog '{selectedSlot.CharacterKey}'.");
            }
        }

        result.StaticBlockers = NormalizeValidationBlockers(result.StaticBlockers);
        result.ReadinessBlockers = NormalizeValidationBlockers(result.ReadinessBlockers);
        result.ScheduleBlockers = NormalizeValidationBlockers(result.ScheduleBlockers);
        return result;
    }

    private static DadSchedulerWakePolicy ResolveSelectedWakePolicy(
        DadPlannerGroup group,
        DadPresetCharacterSlot selectedSlot)
    {
        var rows = DadPlannerSlotRules.GetRowsForSlot(group.Slots, selectedSlot.SlotId);
        var matching = rows.FirstOrDefault(row =>
            row.IsSubstitute == selectedSlot.IsSubstitution &&
            (row.RequiredAccountKey.IsEmpty ||
             string.Equals(row.RequiredAccountKey.Value, selectedSlot.RequiredAccountKey.Value, StringComparison.OrdinalIgnoreCase)) &&
            (row.RequiredCharacterKey.IsEmpty ||
             string.Equals(row.RequiredCharacterKey.Value, selectedSlot.CharacterKey, StringComparison.OrdinalIgnoreCase)));
        return matching?.WakePolicy
               ?? rows.FirstOrDefault(static row => !row.IsSubstitute)?.WakePolicy
               ?? DadSchedulerWakePolicy.LaunchIfOffline;
    }

    private static bool IsLiveReadinessBlocker(string blocker)
        => blocker.Contains("not live, ready, and post-AR ready", StringComparison.OrdinalIgnoreCase)
           || blocker.Contains("account is live as", StringComparison.OrdinalIgnoreCase)
           || blocker.Contains("XADB-only/offline", StringComparison.OrdinalIgnoreCase);

    private static List<string> NormalizeValidationBlockers(IEnumerable<string> blockers)
        => blockers
            .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
            .Select(static blocker => blocker.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class WakePolicyValidation
    {
        public List<string> StaticBlockers { get; set; } = [];
        public List<string> ReadinessBlockers { get; set; } = [];
        public List<string> ScheduleBlockers { get; set; } = [];
    }

    private static List<string> BuildPlannerGroupBlockers(
        DadPlannerGroup? selectedGroup,
        IReadOnlyList<DadPresetCharacterSlot> selectedSlots,
        IReadOnlyList<DadAcquiredCharacter> availableCharacters)
    {
        if (selectedGroup == null)
            return [];

        var blockers = new List<string>();
        var normalizedSlots = DadPlannerSlotRules.NormalizeGroupSlots(selectedGroup.Slots);
        foreach (var duplicateAccount in normalizedSlots
                     .Where(static slot => !slot.IsSubstitute)
                     .Select(static slot => slot.RequiredAccountKey.Value)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1)
                     .Select(static group => group.Key))
        {
            blockers.Add($"Planner group '{selectedGroup.DisplayName}' uses account '{duplicateAccount}' in multiple slots; one account can only satisfy one planned slot.");
        }

        foreach (var slot in normalizedSlots.Where(slot =>
                     slot.RequiredAccountKey.IsEmpty &&
                     !TryGetRegisteredIslandIdentityToken(slot, out _)))
            blockers.Add($"Planner group slot '{slot.SlotId}' is missing a required account key.");

        foreach (var slot in selectedSlots.Where(static slot => slot.RequiredJobId.HasValue))
        {
            var requiredJobId = slot.RequiredJobId!.Value;
            if (!string.IsNullOrWhiteSpace(slot.SharedIdentityToken))
            {
                if (!DadRosterCharacterMerge.IsCombatJob(requiredJobId))
                {
                    blockers.Add(
                        $"Planner group slot '{slot.SlotId}' requests class/job {requiredJobId}, which is not a supported combat job.");
                }
                continue;
            }

            var characterLabel = !string.IsNullOrWhiteSpace(slot.CharacterKey)
                ? slot.CharacterKey
                : !slot.RequiredCharacterKey.IsEmpty
                    ? slot.RequiredCharacterKey.Value
                    : "unresolved character";
            var blocker = DadPlannerRequestedJobValidationRules.Validate(slot, availableCharacters) switch
            {
                DadPlannerRequestedJobValidationFailure.None => string.Empty,
                DadPlannerRequestedJobValidationFailure.InvalidCombatJob =>
                    $"Planner group slot '{slot.SlotId}' requests class/job {requiredJobId}, which is not a supported combat job.",
                DadPlannerRequestedJobValidationFailure.ExactCharacterUnavailable =>
                    $"Planner group slot '{slot.SlotId}' cannot validate requested class/job {requiredJobId} because its exact selected character is unavailable.",
                DadPlannerRequestedJobValidationFailure.XadbUnavailable =>
                    $"Planner group slot '{slot.SlotId}' cannot validate requested class/job {requiredJobId} for '{characterLabel}' because no durable exact-character learned-job ledger is available (legacy XADB-unavailable state).",
                DadPlannerRequestedJobValidationFailure.JobUnavailable =>
                    $"Planner group slot '{slot.SlotId}' requests class/job {requiredJobId}, but the exact character's durable learned-job ledger for '{characterLabel}' does not contain it at a positive level.",
                _ => $"Planner group slot '{slot.SlotId}' has an invalid requested class/job selection.",
            };

            if (!string.IsNullOrWhiteSpace(blocker))
                blockers.Add(blocker);
        }

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

    private List<string> BuildSharedIdentityBlockers(DadPlannerGroup? selectedGroup)
    {
        if (selectedGroup == null)
            return [];

        var bindings = currentRemoteBindingsProvider()
            .Where(static binding => binding != null)
            .Select(static binding => binding.Clone().Normalize())
            .Where(static binding => binding.IsValid)
            .ToList();
        var hasInvalidSharedSlot = selectedGroup.Slots.Any(slot =>
            slot.SharedIdentity != null &&
            (!TryGetRegisteredIslandIdentityToken(slot, out var token) ||
             bindings.Count(binding => string.Equals(
                 binding.OpaqueCharacterId,
                 token,
                 StringComparison.Ordinal)) != 1));
        return hasInvalidSharedSlot || !string.IsNullOrWhiteSpace(selectedGroup.SharedStopTargetIdentityToken)
            ? DadSharedPlanRules.BuildBlockers(selectedGroup)
            : [];
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
                static group => FormatPlannerAccountLabel(group.First()),
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

    private static string FormatPlannerAccountLabel(DadRosterAccountOption option)
    {
        var accountKey = option.AccountKey.Value?.Trim() ?? string.Empty;
        var displayName = !string.IsNullOrWhiteSpace(option.AccountAlias)
            ? option.AccountAlias.Trim()
            : !string.IsNullOrWhiteSpace(option.DisplayName)
                ? option.DisplayName.Trim()
                : accountKey;
        if (string.IsNullOrWhiteSpace(displayName))
            return string.IsNullOrWhiteSpace(accountKey) ? "(account)" : accountKey;

        if (string.IsNullOrWhiteSpace(accountKey) ||
            string.Equals(displayName, accountKey, StringComparison.OrdinalIgnoreCase))
        {
            return displayName;
        }

        return $"{displayName} ({accountKey})";
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
