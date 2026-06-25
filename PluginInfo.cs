namespace dad;

internal static class PluginInfo
{
    public const string DisplayName = "dad";
    public const string InternalName = "dad";
    public const string Command = "/dad";
    public const string Summary = "Private Dad duty operations shell with Dad Coordinator authority, Client Dad workers, preset planning, queue routing, and Duty Support automation.";
    public const string SupportUrl = "https://ko-fi.com/mcvaxius";

    public static readonly string[] Services =
    [
        "ConfigManager",
        "DadPresenceService",
        "DadTransportService",
        "DadCharacterIntelligenceService",
        "DadKrangleService",
        "DadPlannerService",
        "DadClaimService",
        "DadPartyAssemblyService",
        "DadQueueExecutionService",
        "DadModuleRegistry",
        "DadXadbClient",
        "DadPresetProviderService",
        "DadExternalPluginCapabilityService",
        "DadDutyQueueService",
        "DadCoordinatorService",
        "DadIpcService",
        "DadDutyIpcService",
        "DadQuestionableReflectionBridge",
    ];

    public static readonly string[] Phases =
    [
        "Shell and profile startup",
        "Character pool acquisition",
        "Presence and localhost transport",
        "Server authority, leases, and party assembly",
        "Module routing",
        "IPC contract and status surface",
        "Planner/operator polish",
        "Guarded live execution",
    ];

    public static readonly string[] Tests =
    [
        "Load plugin and open both windows",
        "Verify /dad ws and /dad j",
        "Verify Dad IPC ready and orchestration lifecycle",
        "Verify localhost worker discovery and post-AR readiness waits",
        "Verify VERMAXION can start Dad and cancel through Dad Coordinator authority",
        "Verify status names worker role, authority, phase, module, and blockers",
        "Verify Multiplayer surface shows account/character/session/lease authority",
        "Verify transport bind and authority target settings update the listener/authority endpoint surfaces",
        "Verify Preset Planner exposes typed activity/transport/queue/invite controls, account filters, and filter counts",
        "Verify Preset Planner validates typed roster slots, preview-only local tests, and planner summary export",
        "Verify Preset Planner request JSON changes with planner controls and only startable requests can run",
        "Verify Krangle Names toggles operator-facing account and character names without changing run contracts",
        "Verify Questionable bridge patches Dad duty IPC only while Questionable is idle",
    ];
}
