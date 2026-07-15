using System.Text;

namespace dad.Models;

public enum DadQueueTargetKind
{
    DutyFinderDuty,
    Roulette,
}

public sealed class DadQueueTarget
{
    public int SchemaVersion { get; set; } = 1;
    public DadQueueTargetKind Kind { get; set; } = DadQueueTargetKind.DutyFinderDuty;
    public uint ContentFinderConditionId { get; set; }
    public uint RouletteId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public DadQueueTarget Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            Kind = Kind,
            ContentFinderConditionId = ContentFinderConditionId,
            RouletteId = RouletteId,
            Key = Key,
            DisplayName = DisplayName,
        };
}

public sealed class DadRunRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string RequestedBy { get; set; } = string.Empty;
    public DadRunStopPolicy StopPolicy { get; set; } = new();
    public DadPreDutyRepairPolicy PreDutyRepairPolicy { get; set; } = new();
    public DadCompletionActions? CompletionActions { get; set; }
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
    public DadSquadronTask? Squadron { get; set; }
    public DadVariantVvdTask? VariantVvd { get; set; }

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
        if (Squadron != null) count++;
        if (VariantVvd != null) count++;
        return count;
    }

    public string DescribeRequestedWork()
    {
        ApplyOrchestrationDefaults();
        StopPolicy ??= new DadRunStopPolicy();
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
            parts.Add($"Daily Roulette '{DailyMsq.QueueTarget.DisplayName}' #{DailyMsq.QueueTarget.RouletteId}");

        if (Msq != null)
            parts.Add($"MSQ preset '{Msq.Preset}' ({Msq.Attempts} attempt(s) / legacy {Msq.LegacyQueuePreset})");

        if (DutySupport != null)
        {
            var selector = DutySupport.AutoSelectHighestEligible ? "auto-level" : $"{DutySupport.DutyName} #{DutySupport.ContentFinderConditionId}";
            parts.Add($"Duty Support ({selector})");
        }

        if (Trust != null)
        {
            var selector = Trust.AutoSelectHighestEligible ? "auto-level" : $"{Trust.DutyName} #{Trust.ContentFinderConditionId}";
            var refresh = Trust.RefreshNpcLevelsBeforeQueue ? "refresh NPC levels" : "no NPC refresh";
            parts.Add($"Trust ({selector}, {refresh})");
        }

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

        if (Squadron != null)
            parts.Add($"Squadron command mission ({Squadron.DutyName} #{Squadron.ContentFinderConditionId})");

        if (VariantVvd != null)
            parts.Add($"Variant/VVD ({VariantVvd.DutyName} #{VariantVvd.ContentFinderConditionId}, party {VariantVvd.ExpectedPartySize})");

        if (parts.Count == 0)
            return "No dad tasks configured.";

        parts.Add($"authority mode {DadStatusText.FormatAuthorityMode(Orchestration.AuthorityMode)}/transport {Orchestration.TransportMode}/queue {Orchestration.QueueAuthority}/invite {Orchestration.InviteAuthority}/party {Orchestration.RosterIntent.ExpectedPartySize}");
        if (Orchestration.LocalOnlyOverride)
            parts.Add("local-only");
        if (Orchestration.RequiredAccountKeys.Count > 0)
            parts.Add($"accounts {string.Join(", ", Orchestration.RequiredAccountKeys.Select(static key => key.ToString()))}");
        if (Orchestration.RequiredCharacterKeys.Count > 0)
            parts.Add($"characters {string.Join(", ", Orchestration.RequiredCharacterKeys.Select(static key => key.ToString()))}");
        parts.Add($"stop {StopPolicy.Normalize().Describe()}");

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
        StopPolicy ??= new DadRunStopPolicy();
        StopPolicy.Normalize();
        PreDutyRepairPolicy ??= new DadPreDutyRepairPolicy();
        PreDutyRepairPolicy.Normalize();
        Orchestration ??= new DadOrchestrationIntent();
        Orchestration.RosterIntent ??= new DadRosterIntent();
        Orchestration.WaitPolicy ??= new DadRunWaitPolicy();
        Orchestration.PreferredRosterCharacters ??= [];
        Orchestration.RequiredRosterCharacters ??= [];
        Orchestration.PreferredAccountKeys ??= [];
        Orchestration.RequiredAccountKeys ??= [];
        Orchestration.PreferredCharacterKeys ??= [];
        Orchestration.RequiredCharacterKeys ??= [];
        Orchestration.PreferredInviterCharacterKey = new DadCharacterKey((Orchestration.PreferredInviterCharacterKey.Value ?? string.Empty).Trim());
        Orchestration.PreferredLeaderCharacterKey = new DadCharacterKey((Orchestration.PreferredLeaderCharacterKey.Value ?? string.Empty).Trim());

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
                _ when Squadron != null => DadModuleId.Squadron,
                _ when VariantVvd != null => DadModuleId.VariantVvd,
                _ => DadModuleId.Duty,
            };
        }

        var inferredPartySize = DetermineExpectedPartySize();
        if (Orchestration.RosterIntent.ExpectedPartySize <= 0 ||
            (Orchestration.RosterIntent.ExpectedPartySize == 1 && inferredPartySize > 1))
        {
            Orchestration.RosterIntent.ExpectedPartySize = inferredPartySize;
        }

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
                ? DadTransportMode.ServerHub
                : DadTransportMode.LocalOnly;

            if (Orchestration.QueueAuthority == DadQueueAuthority.LocalOnly &&
                Orchestration.RosterIntent.RequireRemoteParticipants)
            {
                Orchestration.QueueAuthority = DadQueueAuthority.Leader;
            }
        }

        if (Orchestration.RosterIntent.RequireRemoteParticipants &&
            Orchestration.InviteAuthority == DadInviteAuthority.NotNeeded)
        {
            Orchestration.InviteAuthority = DadInviteAuthority.PresetLeader;
        }

        if (Orchestration.InviteAuthority == DadInviteAuthority.PresetLeader &&
            Orchestration.PreferredInviterCharacterKey.IsEmpty)
        {
            Orchestration.PreferredInviterCharacterKey = Orchestration.PreferredLeaderCharacterKey;
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
                    DadModuleId.DailyMsq => "DailyRoulette",
                    DadModuleId.Blunderville => "Blunderville",
                    DadModuleId.Mogtome => "Mogtome",
                    DadModuleId.Commendation => "CommendationAuraLane",
                    DadModuleId.Astrope => "AstropeAuraLane",
                    DadModuleId.CustomDuty => "CustomDuty",
                    DadModuleId.Squadron => "Squadron",
                    DadModuleId.VariantVvd => "VariantVvd",
                    _ => Orchestration.LocalOnlyOverride ? "LocalOnly" : "ServerDad",
                };
        }

        return Orchestration;
    }

    private int DetermineExpectedPartySize()
    {
        // B3 (Option A, reversible): MOGTOME is a solo helper-IPC lane, so it is no longer part of the
        // 4-person group and falls through to the default party size of 1. Re-add `|| Mogtome != null`
        // to restore the legacy 4-person premade topology.
        if (DailyMsq != null || Commendation != null || Astrope != null)
            return 4;

        if (Msq != null)
            return 1;

        if (PremadeDuty != null)
            return Math.Max(1, PremadeDuty.ExpectedPartySize);

        if (CustomDuty != null)
            return Math.Clamp(CustomDuty.ExpectedPartySize, 1, 8);

        if (VariantVvd != null)
            return Math.Clamp(VariantVvd.ExpectedPartySize, 1, 4);

        if (Dungeon?.QueueViaLanParty == true)
            return 4;

        return 1;
    }
}

public sealed class DadRunStopPolicy
{
    public const int DefaultTargetLevel = 100;
    public const int DefaultSafetyCap = 20;

    public DadPlannerStopMode Mode { get; set; } = DadPlannerStopMode.AfterRuns;
    public int AfterRuns { get; set; } = 1;
    public int TargetLevel { get; set; } = DefaultTargetLevel;
    public DadCharacterKey TargetCharacterKey { get; set; } = new(string.Empty);
    public string TargetCharacterLabel { get; set; } = string.Empty;
    public int SafetyCap { get; set; } = DefaultSafetyCap;
    public uint StopItemId { get; set; }              // feature batch A: ItemTarget mode
    public int StopItemTargetCount { get; set; } = 1; // feature batch A: ItemTarget mode

    public DadRunStopPolicy Normalize()
    {
        AfterRuns = Math.Clamp(AfterRuns <= 0 ? 1 : AfterRuns, 1, 200);
        TargetLevel = Math.Clamp(TargetLevel <= 0 ? DefaultTargetLevel : TargetLevel, 1, 999);
        SafetyCap = Math.Clamp(SafetyCap <= 0 ? DefaultSafetyCap : SafetyCap, 1, 200);
        StopItemTargetCount = Math.Clamp(StopItemTargetCount <= 0 ? 1 : StopItemTargetCount, 1, 99999);
        TargetCharacterKey = new DadCharacterKey((TargetCharacterKey.Value ?? string.Empty).Trim());
        TargetCharacterLabel = TargetCharacterLabel?.Trim() ?? string.Empty;
        return this;
    }

    public DadRunStopPolicy Clone()
        => new()
        {
            Mode = Mode,
            AfterRuns = AfterRuns,
            TargetLevel = TargetLevel,
            TargetCharacterKey = TargetCharacterKey,
            TargetCharacterLabel = TargetCharacterLabel,
            SafetyCap = SafetyCap,
            StopItemId = StopItemId,
            StopItemTargetCount = StopItemTargetCount,
        };

    public int GetSafetyCap()
        => Mode == DadPlannerStopMode.AfterRuns
            ? Math.Max(1, AfterRuns)
            : Math.Max(1, SafetyCap);

    public string Describe()
        => Mode switch
        {
            DadPlannerStopMode.TargetLevel => TargetCharacterKey.IsEmpty
                ? $"target level {TargetLevel} for selected character, safety cap {GetSafetyCap()} run(s)"
                : $"target level {TargetLevel} for {TargetCharacterKey}, safety cap {GetSafetyCap()} run(s)",
            DadPlannerStopMode.ItemTarget => $"until item {StopItemId} reaches {Math.Max(1, StopItemTargetCount)}, safety cap {GetSafetyCap()} run(s)",
            DadPlannerStopMode.RestedXpDepleted => $"until rested XP is depleted, safety cap {GetSafetyCap()} run(s)",
            _ => $"{Math.Max(1, AfterRuns)} run(s)",
        };
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
    public DadQueueTarget QueueTarget { get; set; } = new()
    {
        Kind = DadQueueTargetKind.Roulette,
        Key = "MainScenario",
        DisplayName = "Main Scenario Roulette",
    };
}

public sealed class DadMsqTask
{
    public string Preset { get; set; } = "MSQ";
    public string LegacyQueuePreset { get; set; } = "Daily MSQ";
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
    public bool PreferTrustThenDutySupport { get; set; } = true;
}

public sealed class DadDutySupportTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
    public bool AutoSelectHighestEligible { get; set; }
}

public sealed class DadTrustTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
    public bool AutoSelectHighestEligible { get; set; }
    public bool RefreshNpcLevelsBeforeQueue { get; set; } = true;
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
    public DadQueueTarget QueueTarget { get; set; } = new()
    {
        Kind = DadQueueTargetKind.DutyFinderDuty,
        DisplayName = "Under the Armour",
    };
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = "Under the Armour";
    public int Attempts { get; set; } = 1;
    public string StopMode { get; set; } = DadCommendationStopModes.Attempts;
    public int TargetTotalCommendations { get; set; }
    public int TargetGainedCommendations { get; set; }
}

public sealed class DadAstropeTask
{
    public DadQueueTarget QueueTarget { get; set; } = new()
    {
        Kind = DadQueueTargetKind.Roulette,
        Key = "Mentor",
        DisplayName = "Mentor Roulette",
    };
    public int Attempts { get; set; } = 1;
    public DadTimeWindow ValidLocalTimeWindow { get; set; } = new();
}

public sealed class DadCustomDutyTask
{
    public DadQueueTarget QueueTarget { get; set; } = new();
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int ExpectedPartySize { get; set; } = 1;
    public bool Unsynced { get; set; }
    public int Attempts { get; set; } = 1;
}

public sealed class DadSquadronTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int Attempts { get; set; } = 1;
}

public sealed class DadVariantVvdTask
{
    public uint ContentFinderConditionId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public int ExpectedPartySize { get; set; } = 1;
    public bool Unsynced { get; set; }
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

public static class DadCommendationStopModes
{
    public const string Attempts = "Attempts";
    public const string TargetTotal = "TargetTotal";
    public const string TargetGained = "TargetGained";
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
