using dad.Models;

namespace dad.Services;

public sealed class DadModuleRegistry
{
    private readonly Dictionary<DadModuleId, DadModuleCapabilitySnapshot> capabilities;

    public DadModuleRegistry()
    {
        capabilities = new Dictionary<DadModuleId, DadModuleCapabilitySnapshot>
        {
            [DadModuleId.Duty] = BuildCapability(DadModuleId.Duty, "Duty", 1, false, true, true, "Local Duty / Unsync native Duty Finder queue enabled with guarded content validation, queue entry, completion, exit, and stabilization tracking.", string.Empty, canStartQueue: true, canTrackCompletion: true, canExecuteLiveQueue: true),
            [DadModuleId.Msq] = BuildCapability(DadModuleId.Msq, "MSQ", 4, true, false, true, "MSQ Phase 5 readiness/status prepared; live queue start remains deferred pending preset/roulette policy.", "MSQ live queue start remains deferred until preset/roulette queue policy is proven."),
            [DadModuleId.DutySupport] = BuildCapability(DadModuleId.DutySupport, "Duty Support", 1, false, true, false, "Duty Support native queue enabled with selectable FrenRider, ADS force-command, or user-owned in-duty mode.", string.Empty, canStartQueue: true, canTrackCompletion: true, canExecuteLiveQueue: true),
            [DadModuleId.Trust] = BuildCapability(DadModuleId.Trust, "Trust", 1, false, true, false, "Trust native queue enabled with conservative DawnContent validation and FrenRider/user-owned in-duty observation.", string.Empty, canStartQueue: true, canTrackCompletion: true, canExecuteLiveQueue: true),
            [DadModuleId.PremadeDuty] = BuildCapability(DadModuleId.PremadeDuty, "Premade Duty", 4, true, false, true, "Premade Duty guarded regular Duty Finder queue enabled for Dad-verified full parties; live party roster validation remains manual follow-up.", string.Empty, canStartQueue: true, canTrackCompletion: true, canExecuteLiveQueue: true),
            [DadModuleId.DailyMsq] = BuildCapability(DadModuleId.DailyMsq, "Daily MSQ", 4, true, false, true, "Premade Daily MSQ orchestration ready.", "Premade Daily MSQ queue start is not enabled yet."),
            [DadModuleId.Blunderville] = BuildCapability(DadModuleId.Blunderville, "Blunderville", 1, false, true, false, "Blunderville lane planning ready.", "Blunderville executor is not enabled yet."),
            [DadModuleId.Mogtome] = BuildCapability(DadModuleId.Mogtome, "MOGTOME", 4, true, false, true, "MOGTOME helper lane planning ready.", "MOGTOME helper queue handoff is not enabled yet."),
            [DadModuleId.Commendation] = BuildCapability(DadModuleId.Commendation, "Commendation", 4, true, false, true, "Commendation orchestration ready.", "Commendation queue/task start is not enabled yet."),
            [DadModuleId.Astrope] = BuildCapability(DadModuleId.Astrope, "Astrope", 4, true, false, true, "Astrope orchestration ready.", "Astrope queue/task start is not enabled yet."),
            [DadModuleId.CustomDuty] = BuildCapability(DadModuleId.CustomDuty, "Custom Duty", 1, false, true, true, "Custom duty planning ready.", "Custom duty executor is not enabled yet."),
        };
    }

    public DadModuleCapabilitySnapshot GetCapability(DadModuleId moduleId)
        => capabilities.TryGetValue(moduleId, out var capability)
            ? new DadModuleCapabilitySnapshot
            {
                ModuleId = capability.ModuleId,
                DisplayName = capability.DisplayName,
                OwnerLabel = capability.OwnerLabel,
                RequiredPartySize = capability.RequiredPartySize,
                RequiresPeers = capability.RequiresPeers,
                SupportsLocalOnly = capability.SupportsLocalOnly,
                SupportsPremade = capability.SupportsPremade,
                CanPlan = capability.CanPlan,
                CanAssembleParty = capability.CanAssembleParty,
                CanStartQueue = capability.CanStartQueue,
                CanTrackCompletion = capability.CanTrackCompletion,
                CanRequeue = capability.CanRequeue,
                CanExecuteLiveQueue = capability.CanExecuteLiveQueue,
                CurrentStatus = capability.CurrentStatus,
                Notes = capability.Notes,
                Blockers = capability.Blockers.Select(static blocker => blocker.Clone()).ToList(),
            }
            : new DadModuleCapabilitySnapshot
            {
                ModuleId = moduleId,
                DisplayName = moduleId.ToString(),
                OwnerLabel = "Unknown",
                CurrentStatus = "No module capability recorded.",
                Blockers =
                [
                    BuildBlocker(moduleId, "CanPlan", "No module capability recorded.", DadModuleBlockerSeverity.Blocked),
                ],
            };

    public DadModuleCapabilityQueryResult GetCapabilities()
        => new()
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Modules = capabilities.Keys
                .OrderBy(static key => key)
                .Select(GetCapability)
                .ToList(),
        };

    private static DadModuleBlockerDto BuildBlocker(
        DadModuleId moduleId,
        string capability,
        string summary,
        DadModuleBlockerSeverity severity = DadModuleBlockerSeverity.Deferred)
        => new()
        {
            ModuleId = moduleId,
            Capability = capability,
            Severity = severity,
            Summary = summary,
        };

    private static DadModuleCapabilitySnapshot BuildCapability(
        DadModuleId moduleId,
        string displayName,
        int partySize,
        bool requiresPeers,
        bool supportsLocalOnly,
        bool supportsPremade,
        string currentStatus,
        string queueBlocker,
        bool canStartQueue = false,
        bool canTrackCompletion = false,
        bool canRequeue = false,
        bool canExecuteLiveQueue = false)
    {
        var blockers = new List<DadModuleBlockerDto>();
        if (!canStartQueue && !string.IsNullOrWhiteSpace(queueBlocker))
            blockers.Add(BuildBlocker(moduleId, "CanStartQueue", queueBlocker));

        if (!canTrackCompletion)
            blockers.Add(BuildBlocker(moduleId, "CanTrackCompletion", $"{displayName} completion tracking is not enabled yet."));

        if (!canRequeue)
            blockers.Add(BuildBlocker(moduleId, "CanRequeue", $"{displayName} requeue/retry loop is not enabled yet."));

        return new DadModuleCapabilitySnapshot
        {
            ModuleId = moduleId,
            DisplayName = displayName,
            OwnerLabel = "Dad",
            RequiredPartySize = partySize,
            RequiresPeers = requiresPeers,
            SupportsLocalOnly = supportsLocalOnly,
            SupportsPremade = supportsPremade,
            CanPlan = true,
            CanAssembleParty = true,
            CanStartQueue = canStartQueue,
            CanTrackCompletion = canTrackCompletion,
            CanRequeue = canRequeue,
            CanExecuteLiveQueue = canExecuteLiveQueue,
            CurrentStatus = currentStatus,
            Notes = canExecuteLiveQueue
                ? "Dad owns planning, authority, routing, and native live execution for this guarded lane."
                : "Dad owns planning, authority, and routing for this lane. Guarded live execution remains deferred until the lane executor is enabled.",
            Blockers = blockers,
        };
    }
}
