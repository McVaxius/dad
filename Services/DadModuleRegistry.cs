using dad.Models;

namespace dad.Services;

public sealed class DadModuleRegistry
{
    private readonly Dictionary<DadModuleId, DadModuleCapabilitySnapshot> capabilities;

    public DadModuleRegistry()
    {
        capabilities = new Dictionary<DadModuleId, DadModuleCapabilitySnapshot>
        {
            [DadModuleId.Duty] = BuildCapability(DadModuleId.Duty, "Duty", 1, false, true, true, "Duty planning and lane selection ready.", "Guarded Duty Finder queue start is not enabled yet."),
            [DadModuleId.Msq] = BuildCapability(DadModuleId.Msq, "MSQ", 4, true, false, true, "MSQ lane planning ready.", "MSQ queue executor is not enabled yet."),
            [DadModuleId.DutySupport] = BuildCapability(DadModuleId.DutySupport, "Duty Support", 1, false, true, false, "Duty Support lane planning ready.", "Duty Support queue executor is not enabled yet."),
            [DadModuleId.Trust] = BuildCapability(DadModuleId.Trust, "Trust", 1, false, true, false, "Trust lane planning ready.", "Trust queue executor is not enabled yet."),
            [DadModuleId.PremadeDuty] = BuildCapability(DadModuleId.PremadeDuty, "Premade Duty", 4, true, false, true, "Premade duty planning ready.", "Premade duty queue executor is not enabled yet."),
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
        string queueBlocker)
        => new()
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
            CanStartQueue = false,
            CanTrackCompletion = false,
            CanRequeue = false,
            CanExecuteLiveQueue = false,
            CurrentStatus = currentStatus,
            Notes = "Dad owns planning, authority, and routing for this lane. Guarded live execution remains deferred until the lane executor is enabled.",
            Blockers =
            [
                BuildBlocker(moduleId, "CanStartQueue", queueBlocker),
                BuildBlocker(moduleId, "CanTrackCompletion", $"{displayName} completion tracking is not enabled yet."),
                BuildBlocker(moduleId, "CanRequeue", $"{displayName} requeue/retry loop is not enabled yet."),
            ],
        };
}
