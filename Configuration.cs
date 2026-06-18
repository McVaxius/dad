using Dalamud.Configuration;
using dad.Models;

namespace dad;

public enum DadCombatRotationMode
{
    UseFrenRider = 0,
    ForceCommands = 1,
    DoNothing = 2,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool PluginEnabled { get; set; } = false;
    public bool RunAsServerDad { get; set; } = true;
    public bool LocalOnlyModeEnabled { get; set; }
    public bool DebugUiEnabled { get; set; }
    public bool KrangleOperatorNamesEnabled { get; set; }
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = "\uE044";
    public string DtrIconDisabled { get; set; } = "\uE04C";
    public string ClientAccountId { get; set; } = string.Empty;
    public string LastAccountId { get; set; } = string.Empty;
    public string TransportBindHost { get; set; } = string.Empty;
    public int TransportBindPort { get; set; }
    public string AuthorityTargetHost { get; set; } = string.Empty;
    public int AuthorityTargetPort { get; set; }
    public int ParticipantReadyTimeoutSeconds { get; set; } = 300;
    public int AssemblyTimeoutSeconds { get; set; } = 120;
    public int HeartbeatStaleSeconds { get; set; } = 12;
    public int LeaseDurationSeconds { get; set; } = 20;
    public int CancelAckTimeoutSeconds { get; set; } = 6;
    public DadCombatRotationMode CombatRotationMode { get; set; } = DadCombatRotationMode.UseFrenRider;
    public DadPresetPlannerOptions PlannerOptions { get; set; } = new();
    public List<DadPlannerGroup> PlannerGroups { get; set; } = [];
    public List<DadLaunchProfile> LaunchProfiles { get; set; } = [];
    public List<DadProfileCatalog> ProfileCatalogCache { get; set; } = [];
    public List<DadRunResult> RunHistory { get; set; } = [];
    public DadRunResult? PersistedActiveRun { get; set; }
    public DadCharacterLoadInstruction CharacterLoadInstruction { get; set; } = new();
    public DadRosterCatalogConfiguration RosterCatalog { get; set; } = new();
    public List<DadScheduledCrewJob> SchedulerQueue { get; set; } = [];
    public List<DadScheduledCrewJobResult> SchedulerHistory { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
