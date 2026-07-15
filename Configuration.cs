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
    public int Version { get; set; } = 3;
    public bool PluginEnabled { get; set; } = false;
    public bool RunAsServerDad { get; set; } = false;
    public bool LocalOnlyModeEnabled { get; set; }
    public bool DebugUiEnabled { get; set; }
    public bool SetupWizardLoaded { get; set; }
    public bool KrangleOperatorNamesEnabled { get; set; }
    public bool ShowCharacterConflictSummary { get; set; }
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = "\uE044";
    public string DtrIconDisabled { get; set; } = "\uE04C";
    // Review M18 — config/account-store boundary:
    //   * This Configuration blob owns planner/scheduler/run state: PlannerGroups, LaunchProfiles,
    //     ProfileCatalogCache, RunHistory, SchedulerQueue/History, RosterCatalog, and the account *identity*
    //     pointers below (ClientAccountId / LastAccountId).
    //   * ConfigManager owns the per-account character roster (one {accountId}_dad.json file per account).
    //   Account add/delete/merge must update BOTH stores; that scrub logic lives in Plugin (ClearAllDadAccountData
    //   / DeleteDadAccount). Keep this split in mind — do not duplicate character rosters into this blob.
    public string ClientAccountId { get; set; } = string.Empty;
    public string LastAccountId { get; set; } = string.Empty;
    public string ServerListenHost { get; set; } = "127.0.0.1";
    public int ServerListenPort { get; set; } = 4647;
    public string ServerDadHost { get; set; } = "127.0.0.1";
    public int ServerDadPort { get; set; } = 4647;
    // Legacy migration fields. Runtime transport does not read them after Version 3 migration.
    public string TransportBindHost { get; set; } = string.Empty;
    public int TransportBindPort { get; set; }
    public string AuthorityTargetHost { get; set; } = string.Empty;
    public int AuthorityTargetPort { get; set; }
    // Review C2(b): optional shared secret. When set, every transport envelope carries an HMAC and inbound
    // envelopes are rejected unless the HMAC matches — authenticates peers. Empty = no auth (loopback default).
    public string TransportSharedSecret { get; set; } = string.Empty;
    public int ParticipantReadyTimeoutSeconds { get; set; } = 300;
    public int VermaxionHoldTimeoutSeconds { get; set; } = 5400;
    public int AutoRetainerBusyTimeoutSeconds { get; set; } = 1200;
    public int AssemblyTimeoutSeconds { get; set; } = 120;
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int HeartbeatStaleSeconds { get; set; } = 15;
    public int PeerCatalogRefreshIntervalSeconds { get; set; } = 60;
    public int LeaseDurationSeconds { get; set; } = 20;
    public int CancelAckTimeoutSeconds { get; set; } = 6;
    public DadCombatRotationMode CombatRotationMode { get; set; } = DadCombatRotationMode.UseFrenRider;
    public DadPreDutyRepairPolicy PreDutyRepairPolicy { get; set; } = new();
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
    public List<DadScheduleDefinition> Schedules { get; set; } = [];
    public DadScheduleRunState ActiveScheduleRun { get; set; } = new();
    public List<DadScheduleRunResult> ScheduleHistory { get; set; } = [];

    // Review L3: was a machine-specific hardcoded const in DadSchedulerService; now operator-configurable.
    public string ClientBootDirectory { get; set; } = @"Z:\!ff14clientboot";

    // Review C2: secure-by-default gate for executing peer-supplied character-load commands over the
    // transport. Off by default — a remote peer can no longer drive arbitrary slash commands unless the
    // operator explicitly opts in. (Full HMAC envelope auth is tracked as follow-up work.)
    public bool AllowRemoteCommandExecution { get; set; } = false;

    // Feature batch A (dadfeatures20260620b):
    // /dad advanced gate - when on, legacy/advanced options are shown.
    public bool AdvancedModeEnabled { get; set; } = false;
    // AutoDuty-style party validation override — lets a run start despite party-composition validation. Default off.
    public bool PartyValidationOverrideEnabled { get; set; } = false;
    // Actions to run when a Dad run completes. Legacy kill modes are kept for config compatibility but disabled.
    public DadCompletionActions CompletionActions { get; set; } = new();

    // Review M19: operator opt-out for the Questionable reflection bridge (invasive runtime field patching).
    // Default on (preserves the AutoDuty/ADS handoff); disabling restores any patched values and stops it.
    public bool QuestionableBridgeEnabled { get; set; } = true;

    public bool MigrateTransportSettings()
    {
        var changed = false;
        if (Version < 3)
        {
            if (!string.IsNullOrWhiteSpace(TransportBindHost))
                ServerListenHost = TransportBindHost.Trim();
            if (TransportBindPort is > 0 and <= 65535)
                ServerListenPort = TransportBindPort;
            if (!string.IsNullOrWhiteSpace(AuthorityTargetHost))
                ServerDadHost = AuthorityTargetHost.Trim();
            if (AuthorityTargetPort is > 0 and <= 65535)
                ServerDadPort = AuthorityTargetPort;

            Version = 3;
            changed = true;
        }

        var serverListen = NormalizeEndpoint(ServerListenHost, ServerListenPort);
        ServerListenHost = serverListen.Host;
        ServerListenPort = serverListen.Port;
        changed |= serverListen.Changed;

        var serverDad = NormalizeEndpoint(ServerDadHost, ServerDadPort);
        ServerDadHost = serverDad.Host;
        ServerDadPort = serverDad.Port;
        changed |= serverDad.Changed;

        var heartbeatInterval = NormalizeMinimum(HeartbeatIntervalSeconds, 5, 2);
        changed |= heartbeatInterval != HeartbeatIntervalSeconds;
        HeartbeatIntervalSeconds = heartbeatInterval;

        var heartbeatStale = NormalizeMinimum(HeartbeatStaleSeconds, 15, Math.Max(3, HeartbeatIntervalSeconds * 3));
        changed |= heartbeatStale != HeartbeatStaleSeconds;
        HeartbeatStaleSeconds = heartbeatStale;

        var peerCatalogRefresh = NormalizeMinimum(PeerCatalogRefreshIntervalSeconds, 60, 10);
        changed |= peerCatalogRefresh != PeerCatalogRefreshIntervalSeconds;
        PeerCatalogRefreshIntervalSeconds = peerCatalogRefresh;

        var vermaxionHold = NormalizeMinimum(VermaxionHoldTimeoutSeconds, 5400, 3600);
        changed |= vermaxionHold != VermaxionHoldTimeoutSeconds;
        VermaxionHoldTimeoutSeconds = vermaxionHold;

        var autoRetainerBusy = NormalizeMinimum(AutoRetainerBusyTimeoutSeconds, 1200, 60);
        changed |= autoRetainerBusy != AutoRetainerBusyTimeoutSeconds;
        AutoRetainerBusyTimeoutSeconds = autoRetainerBusy;

        if (PreDutyRepairPolicy == null)
        {
            PreDutyRepairPolicy = new DadPreDutyRepairPolicy();
            changed = true;
        }
        else
        {
            var priorThreshold = PreDutyRepairPolicy.ThresholdPercent;
            var priorMode = PreDutyRepairPolicy.Mode;
            PreDutyRepairPolicy.Normalize();
            changed |= priorThreshold != PreDutyRepairPolicy.ThresholdPercent ||
                       priorMode != PreDutyRepairPolicy.Mode;
        }
        return changed;
    }

    private static (string Host, int Port, bool Changed) NormalizeEndpoint(string host, int port)
    {
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        var normalizedPort = port is > 0 and <= 65535 ? port : 4647;
        var changed = !string.Equals(host, normalizedHost, StringComparison.Ordinal) || port != normalizedPort;
        return (normalizedHost, normalizedPort, changed);
    }

    private static int NormalizeMinimum(int value, int defaultValue, int minimum)
    {
        return Math.Max(minimum, value <= 0 ? defaultValue : value);
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
