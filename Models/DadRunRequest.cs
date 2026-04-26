using System.Text;

namespace dad.Models;

public sealed class DadRunRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string RequestedBy { get; set; } = string.Empty;
    public DadOrchestrationIntent Orchestration { get; set; } = new();
    public DadDungeonTask? Dungeon { get; set; }
    public DadMsqTask? Msq { get; set; }
    public DadDutySupportTask? DutySupport { get; set; }
    public DadTrustTask? Trust { get; set; }
    public DadPremadeDutyTask? PremadeDuty { get; set; }
    public DadDailyMsqTask? DailyMsq { get; set; }
    public DadBlundervilleTask? Blunderville { get; set; }
    public DadMogtomeTask? Mogtome { get; set; }
    public DadCommendationTask? Commendation { get; set; }
    public DadAstropeTask? Astrope { get; set; }
    public DadCustomDutyTask? CustomDuty { get; set; }

    public int GetConfiguredTaskCount()
    {
        var count = 0;
        if (Dungeon != null) count++;
        if (Msq != null) count++;
        if (DutySupport != null) count++;
        if (Trust != null) count++;
        if (PremadeDuty != null) count++;
        if (DailyMsq != null) count++;
        if (Blunderville != null) count++;
        if (Mogtome != null) count++;
        if (Commendation != null) count++;
        if (Astrope != null) count++;
        if (CustomDuty != null) count++;
        return count;
    }

    public string DescribeRequestedWork()
    {
        ApplyOrchestrationDefaults();
        var parts = new List<string>();

        if (Dungeon != null)
        {
            var job = string.IsNullOrWhiteSpace(Dungeon.SelectedJob) ? "current job" : Dungeon.SelectedJob;
            var syncMode = Dungeon.Unsynced ? "unsynced" : "synced";
            var queueMode = Dungeon.QueueViaLanParty ? "internal premade queue" : "Dad regular Duty Finder queue";
            var preference = string.IsNullOrWhiteSpace(Dungeon.ExecutionPreference)
                ? DadRunRequestOptions.TrustThenDutySupport
                : Dungeon.ExecutionPreference;
            parts.Add($"{Dungeon.Count}x dungeon ({Dungeon.SelectedDungeon} #{Dungeon.ContentFinderConditionId} / {job} / {syncMode} / {Dungeon.Frequency} / {preference} / {queueMode})");
        }

        if (DailyMsq != null)
            parts.Add($"Daily MSQ preset '{DailyMsq.LanPartyPreset}'");

        if (Msq != null)
            parts.Add($"MSQ preset '{Msq.Preset}' ({Msq.Attempts} attempt(s) / legacy {Msq.LegacyQueuePreset})");

        if (DutySupport != null)
            parts.Add($"Duty Support ({DutySupport.DutyName} #{DutySupport.ContentFinderConditionId})");

        if (Trust != null)
            parts.Add($"Trust ({Trust.DutyName} #{Trust.ContentFinderConditionId})");

        if (PremadeDuty != null)
        {
            var syncMode = PremadeDuty.Unsynced ? "unsynced" : "synced";
            parts.Add($"Premade duty ({PremadeDuty.DutyName} #{PremadeDuty.ContentFinderConditionId} / {syncMode} / party {PremadeDuty.ExpectedPartySize})");
        }

        if (Blunderville != null)
        {
            var emote = string.IsNullOrWhiteSpace(Blunderville.EmoteCommand)
                ? "configured emote"
                : Blunderville.EmoteCommand;
            parts.Add($"Blunderville {Blunderville.Mode} ({emote}, {Blunderville.Attempts} attempt(s), {Blunderville.CompletionPolicy})");
        }

        if (Mogtome != null)
            parts.Add($"MOGTOME preset '{Mogtome.Preset}' ({Mogtome.Attempts} attempt(s), {Mogtome.DutyPolicy})");

        if (Commendation != null)
            parts.Add($"{Commendation.Attempts} commendation attempt(s)");

        if (Astrope != null)
            parts.Add($"{Astrope.Attempts} Astrope attempt(s) in {Astrope.ValidLocalTimeWindow.Describe()}");

        if (CustomDuty != null)
            parts.Add($"Custom duty ({CustomDuty.DutyName} #{CustomDuty.ContentFinderConditionId})");

        if (parts.Count == 0)
            return "No dad tasks configured.";

        parts.Add($"authority mode {DadStatusText.FormatAuthorityMode(Orchestration.AuthorityMode)}/transport {Orchestration.TransportMode}/queue {Orchestration.QueueAuthority}/party {Orchestration.RosterIntent.ExpectedPartySize}");
        if (Orchestration.LocalOnlyOverride)
            parts.Add("local-only");
        if (Orchestration.RequiredAccountKeys.Count > 0)
            parts.Add($"accounts {string.Join(", ", Orchestration.RequiredAccountKeys.Select(static key => key.ToString()))}");
        if (Orchestration.RequiredCharacterKeys.Count > 0)
            parts.Add($"characters {string.Join(", ", Orchestration.RequiredCharacterKeys.Select(static key => key.ToString()))}");

        var builder = new StringBuilder();
        for (var index = 0; index < parts.Count; index++)
        {
            if (index > 0)
                builder.Append(" | ");

            builder.Append(parts[index]);
        }

        return builder.ToString();
    }

    public DadOrchestrationIntent ApplyOrchestrationDefaults()
    {
        Orchestration ??= new DadOrchestrationIntent();
        Orchestration.RosterIntent ??= new DadRosterIntent();
        Orchestration.WaitPolicy ??= new DadRunWaitPolicy();

        if (Orchestration.ModuleTarget == DadModuleId.None)
        {
            Orchestration.ModuleTarget = GetConfiguredTaskCount() switch
            {
                0 => DadModuleId.None,
                > 1 => DadModuleId.Mixed,
                _ when Msq != null => DadModuleId.Msq,
                _ when DutySupport != null => DadModuleId.DutySupport,
                _ when Trust != null => DadModuleId.Trust,
                _ when PremadeDuty != null => DadModuleId.PremadeDuty,
                _ when DailyMsq != null => DadModuleId.DailyMsq,
                _ when Blunderville != null => DadModuleId.Blunderville,
                _ when Mogtome != null => DadModuleId.Mogtome,
                _ when Commendation != null => DadModuleId.Commendation,
                _ when Astrope != null => DadModuleId.Astrope,
                _ when CustomDuty != null => DadModuleId.CustomDuty,
                _ => DadModuleId.Duty,
            };
        }

        if (Orchestration.RosterIntent.ExpectedPartySize <= 0)
            Orchestration.RosterIntent.ExpectedPartySize = DetermineExpectedPartySize();

        Orchestration.RosterIntent.RequireRemoteParticipants = Orchestration.RosterIntent.ExpectedPartySize > 1;

        var preserveLocalOnlyAuthority = Orchestration.LocalOnlyOverride
                                         || (Orchestration.AuthorityMode == DadAuthorityMode.LocalOnly
                                             && !Orchestration.RosterIntent.RequireRemoteParticipants);

        if (preserveLocalOnlyAuthority)
        {
            Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly;
            Orchestration.TransportMode = DadTransportMode.LocalOnly;
            Orchestration.QueueAuthority = DadQueueAuthority.LocalOnly;
        }
        else
        {
            Orchestration.AuthorityMode = DadAuthorityMode.ServerDad;
            Orchestration.TransportMode = Orchestration.RosterIntent.RequireRemoteParticipants
                ? DadTransportMode.LocalhostHybrid
                : DadTransportMode.LocalOnly;

            if (Orchestration.QueueAuthority == DadQueueAuthority.LocalOnly &&
                Orchestration.RosterIntent.RequireRemoteParticipants)
            {
                Orchestration.QueueAuthority = DadQueueAuthority.Leader;
            }
        }

        if (string.IsNullOrWhiteSpace(Orchestration.ExecutionConstraintSummary))
        {
            Orchestration.ExecutionConstraintSummary = Dungeon != null
                ? Dungeon.QueueViaLanParty
                    ? "InternalPremadeQueue"
                    : Dungeon.Unsynced
                        ? "LocalDutyUnsynced"
                        : "LocalDutySynced"
                : Orchestration.ModuleTarget switch
                {
                    DadModuleId.Msq => "MsqLane",
                    DadModuleId.DutySupport => "DutySupport",
                    DadModuleId.Trust => "Trust",
                    DadModuleId.PremadeDuty => "PremadeDuty",
                    DadModuleId.DailyMsq => "DailyMsqPremade",
                    DadModuleId.Blunderville => "Blunderville",
                    DadModuleId.Mogtome => "Mogtome",
                    DadModuleId.Commendation => "CommendationAuraLane",
                    DadModuleId.Astrope => "AstropeAuraLane",
                    DadModuleId.CustomDuty => "CustomDuty",
                    _ => Orchestration.LocalOnlyOverride ? "LocalOnly" : "ServerDad",
                };
        }

        return Orchestration;
    }

    private int DetermineExpectedPartySize()
    {
        if (Msq != null || DailyMsq != null || Commendation != null || Astrope != null || Mogtome != null)
            return 4;

        if (PremadeDuty != null)
            return Math.Max(1, PremadeDuty.ExpectedPartySize);

        if (Dungeon?.QueueViaLanParty == true)
            return 4;

        return 1;
    }
}

public sealed class DadDungeonTask
{
    public int Count { get; set; } = 1;
    public string Frequency { get; set; } = DadRunRequestOptions.FrequencyPerArRun;
    public uint ContentFinderConditionId { get; set; }
    public string SelectedDungeon { get; set; } = string.Empty;
    public string SelectedJob { get; set; } = string.Empty;
    public string ExecutionPreference { get; set; } = DadRunRequestOptions.TrustThenDutySupport;
    public bool QueueViaLanParty { get; set; }
    public bool Unsynced { get; set; }
}

public sealed class DadDailyMsqTask
{
    public string LanPartyPreset { get; set; } = "Daily MSQ";
}

public sealed class DadMsqTask
{
    public string Preset { get; set; } = "MSQ";
    public string LegacyQueuePreset { get; set; } = "Daily MSQ";
    public int Attempts { get; set; } = 1;
    public bool PreferTrustThenDutySupport { get; set; } = true;
}

public sealed class DadDutySupportTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
}

public sealed class DadTrustTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
}

public sealed class DadPremadeDutyTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public bool Unsynced { get; set; }
    public int ExpectedPartySize { get; set; } = 4;
    public int Attempts { get; set; } = 1;
}

public sealed class DadBlundervilleTask
{
    public string Mode { get; set; } = DadBlundervilleModes.FixedEmoteRun;
    public string EmoteCommand { get; set; } = string.Empty;
    public string CompletionPolicy { get; set; } = DadBlundervillePolicies.FailOrLeaveAfterEmote;
    public int Attempts { get; set; } = 1;
}

public sealed class DadMogtomeTask
{
    public string Preset { get; set; } = "Daily MSQ";
    public string DutyPolicy { get; set; } = DadMogtomeDutyPolicies.PresetHandoff;
    public int Attempts { get; set; } = 1;
}

public sealed class DadCommendationTask
{
    public int Attempts { get; set; } = 1;
}

public sealed class DadAstropeTask
{
    public int Attempts { get; set; } = 1;
    public DadTimeWindow ValidLocalTimeWindow { get; set; } = new();
}

public sealed class DadCustomDutyTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
}

public static class DadBlundervilleModes
{
    public const string FixedEmoteRun = "FixedEmoteRun";
}

public static class DadBlundervillePolicies
{
    public const string FailOrLeaveAfterEmote = "FailOrLeaveAfterEmote";
}

public static class DadMogtomeDutyPolicies
{
    public const string PresetHandoff = "PresetHandoff";
    public const string PreservePresetDuty = "PreservePresetDuty";
    public const string PinnedDutySelection = "PinnedDutySelection";

    public static readonly string[] All =
    [
        PresetHandoff,
        PreservePresetDuty,
        PinnedDutySelection,
    ];
}

public static class DadRunRequestOptions
{
    public const string FrequencyPerArRun = "Per AR run";
    public const string FrequencyDailyReset = "Daily reset";
    public const string FrequencyWeeklyReset = "Weekly reset";
    public const string TrustThenDutySupport = "TrustThenDutySupport";

    public static readonly string[] LanPartyPresetStubs =
    [
        "Daily MSQ",
        "Leveling",
        "Expert",
        "Custom",
    ];

    public static readonly string[] JobHintExamples =
    [
        "PLD",
        "WAR",
        "DRK",
        "GNB",
        "WHM",
        "SCH",
        "AST",
        "SGE",
        "MNK",
        "DRG",
        "NIN",
        "SAM",
        "RPR",
        "BRD",
        "MCH",
        "DNC",
        "BLM",
        "SMN",
        "RDM",
        "PCT",
        "BLU",
    ];

    public static readonly HashSet<string> ValidFrequencies = new(StringComparer.OrdinalIgnoreCase)
    {
        FrequencyPerArRun,
        FrequencyDailyReset,
        FrequencyWeeklyReset,
    };
}
