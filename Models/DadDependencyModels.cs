namespace dad.Models;

public enum DadDependencyState
{
    Checking = 0,
    Ready = 1,
    Missing = 2,
    InstalledNotLoaded = 3,
    UpdateRequired = 4,
}

public sealed class DadDependencyEntry
{
    public string RequirementId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> AcceptedInternalNames { get; set; } = [];
    public DadDependencyState State { get; set; } = DadDependencyState.Checking;
    public string DetectedInternalName { get; set; } = string.Empty;
    public string DetectedVersion { get; set; } = string.Empty;
    public string MinimumVersion { get; set; } = string.Empty;
    public string OperatorSummary { get; set; } = "Checking plugin status.";

    public DadDependencyEntry Clone()
        => new()
        {
            RequirementId = RequirementId ?? string.Empty,
            DisplayName = DisplayName ?? string.Empty,
            AcceptedInternalNames = AcceptedInternalNames == null ? [] : [..AcceptedInternalNames],
            State = State,
            DetectedInternalName = DetectedInternalName ?? string.Empty,
            DetectedVersion = DetectedVersion ?? string.Empty,
            MinimumVersion = MinimumVersion ?? string.Empty,
            OperatorSummary = OperatorSummary ?? string.Empty,
        };
}

public sealed class DadDependencySnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public long Revision { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public DadDependencyState AggregateState { get; set; } = DadDependencyState.Checking;
    public List<DadDependencyEntry> Entries { get; set; } = [];
    public string OperatorSummary { get; set; } = "Checking required plugins.";

    public bool IsReady => SchemaVersion == CurrentSchemaVersion &&
                           AggregateState == DadDependencyState.Ready &&
                           Entries != null &&
                           Entries.Count == DadDependencyRules.Requirements.Count &&
                           DadDependencyRules.Requirements.All(requirement =>
                               Entries.Count(entry =>
                                   entry != null &&
                                   string.Equals(entry.RequirementId, requirement.RequirementId, StringComparison.OrdinalIgnoreCase) &&
                                   entry.State == DadDependencyState.Ready) == 1);

    public DadDependencySnapshot Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            Revision = Revision,
            CheckedAtUtc = CheckedAtUtc,
            AggregateState = AggregateState,
            Entries = Entries?.Where(static entry => entry != null).Select(static entry => entry.Clone()).ToList() ?? [],
            OperatorSummary = OperatorSummary ?? string.Empty,
        };

    public static DadDependencySnapshot CreateChecking(
        long revision = 0,
        DadDependencySnapshot? previous = null,
        string summary = "Checking required plugins.")
    {
        var priorById = (previous?.Entries ?? [])
            .Where(static entry => entry != null && !string.IsNullOrWhiteSpace(entry.RequirementId))
            .GroupBy(static entry => entry.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new DadDependencySnapshot
        {
            Revision = Math.Max(0, revision),
            CheckedAtUtc = previous?.CheckedAtUtc ?? default,
            AggregateState = DadDependencyState.Checking,
            OperatorSummary = summary,
            Entries = DadDependencyRules.Requirements.Select(requirement =>
            {
                priorById.TryGetValue(requirement.RequirementId, out var prior);
                return new DadDependencyEntry
                {
                    RequirementId = requirement.RequirementId,
                    DisplayName = requirement.DisplayName,
                    AcceptedInternalNames = [..requirement.AcceptedInternalNames],
                    MinimumVersion = requirement.MinimumVersion,
                    State = DadDependencyState.Checking,
                    DetectedInternalName = prior?.DetectedInternalName ?? string.Empty,
                    DetectedVersion = prior?.DetectedVersion ?? string.Empty,
                    OperatorSummary = string.IsNullOrWhiteSpace(prior?.OperatorSummary)
                        ? "Checking plugin status."
                        : $"Checking again. Last result: {prior.OperatorSummary}",
                };
            }).ToList(),
        };
    }
}

public sealed record DadDependencyRequirement(
    string RequirementId,
    string DisplayName,
    IReadOnlyList<string> AcceptedInternalNames,
    string MinimumVersion = "");

public sealed record DadInstalledPluginMetadata(
    string InternalName,
    string DisplayName,
    string Version,
    bool IsLoaded,
    bool IsOutdated);

public static class DadDependencyRules
{
    public const string DependencyBlocker =
        "DAD is waiting for every selected client to report all required plugins loaded and current.";

    public static IReadOnlyList<DadDependencyRequirement> Requirements { get; } =
    [
        new("FrenRider", "Fren Rider", ["FrenRider"]),
        new("ADS", "AI Duty Solver", ["ADS"]),
        new("vnavmesh", "vnavmesh", ["vnavmesh"]),
        new("XADatabase", "XA Database", ["XADatabase"], "0.0.0.39"),
        new("XASlave", "XA Slave", ["XASlave"]),
        new("BossMod", "BossMod", ["BossModReborn", "BossMod"]),
    ];

    public static DadDependencySnapshot Evaluate(
        IEnumerable<DadInstalledPluginMetadata>? installedPlugins,
        long revision,
        DateTime checkedAtUtc)
    {
        if (installedPlugins == null)
            return DadDependencySnapshot.CreateChecking(revision, summary: "Plugin metadata is unavailable; checking again.");

        List<DadInstalledPluginMetadata> metadata;
        try
        {
            metadata = installedPlugins.ToList();
        }
        catch
        {
            return DadDependencySnapshot.CreateChecking(revision, summary: "Plugin metadata could not be inspected; checking again.");
        }

        if (metadata.Any(static plugin => plugin == null || string.IsNullOrWhiteSpace(plugin.InternalName)))
            return DadDependencySnapshot.CreateChecking(revision, summary: "Plugin metadata is incomplete; checking again.");

        var entries = Requirements
            .Select(requirement => EvaluateRequirement(requirement, metadata))
            .ToList();
        var aggregate = ResolveAggregate(entries);

        return new DadDependencySnapshot
        {
            Revision = Math.Max(0, revision),
            CheckedAtUtc = checkedAtUtc,
            AggregateState = aggregate,
            Entries = entries,
            OperatorSummary = aggregate == DadDependencyState.Ready
                ? "All required plugins are loaded and current."
                : DependencyBlocker,
        };
    }

    public static DadDependencyEntry EvaluateRequirement(
        DadDependencyRequirement requirement,
        IEnumerable<DadInstalledPluginMetadata> installedPlugins)
    {
        var candidates = installedPlugins
            .Where(plugin => requirement.AcceptedInternalNames.Any(name =>
                string.Equals(name, plugin.InternalName, StringComparison.OrdinalIgnoreCase)))
            .Select(plugin => EvaluateCandidate(requirement, plugin))
            .ToList();

        if (candidates.Count == 0)
        {
            return CreateEntry(
                requirement,
                DadDependencyState.Missing,
                summary: $"{requirement.DisplayName} is not installed.");
        }

        var ready = candidates.FirstOrDefault(static entry => entry.State == DadDependencyState.Ready);
        if (ready != null)
            return ready;

        return candidates
            .OrderBy(static entry => StatusRank(entry.State))
            .ThenBy(static entry => entry.DetectedInternalName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static DadDependencyState ResolveAggregate(IEnumerable<DadDependencyEntry> entries)
    {
        var states = entries.Select(static entry => entry.State).ToList();
        if (states.Count == 0 || states.Contains(DadDependencyState.Checking))
            return DadDependencyState.Checking;
        if (states.All(static state => state == DadDependencyState.Ready))
            return DadDependencyState.Ready;
        if (states.Contains(DadDependencyState.UpdateRequired))
            return DadDependencyState.UpdateRequired;
        if (states.Contains(DadDependencyState.InstalledNotLoaded))
            return DadDependencyState.InstalledNotLoaded;
        return DadDependencyState.Missing;
    }

    public static DadDependencySnapshot NormalizePeerSnapshot(
        DadDependencySnapshot? snapshot,
        DadDependencySnapshot? previous = null)
    {
        if (snapshot == null ||
            snapshot.SchemaVersion != DadDependencySnapshot.CurrentSchemaVersion ||
            snapshot.Entries == null ||
            snapshot.Entries.Count != Requirements.Count ||
            !Enum.IsDefined(snapshot.AggregateState) ||
            snapshot.Entries.Any(static entry =>
                entry == null ||
                string.IsNullOrWhiteSpace(entry.RequirementId) ||
                !Enum.IsDefined(entry.State)) ||
            Requirements.Any(requirement => snapshot.Entries.Count(entry =>
                entry != null &&
                string.Equals(entry.RequirementId, requirement.RequirementId, StringComparison.OrdinalIgnoreCase)) != 1))
        {
            return DadDependencySnapshot.CreateChecking(
                Math.Max(snapshot?.Revision ?? 0, previous?.Revision ?? 0),
                HasDisplayDetails(snapshot) ? snapshot : previous ?? snapshot,
                "This client has not published compatible dependency truth yet.");
        }

        var normalized = snapshot.Clone();
        if (normalized.AggregateState != DadDependencyState.Checking || previous?.Entries == null)
            return normalized;

        var priorById = previous.Entries
            .Where(static entry => entry != null && !string.IsNullOrWhiteSpace(entry.RequirementId))
            .GroupBy(static entry => entry.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in normalized.Entries)
        {
            if (!priorById.TryGetValue(entry.RequirementId, out var prior))
                continue;
            if (string.IsNullOrWhiteSpace(entry.DetectedInternalName))
                entry.DetectedInternalName = prior.DetectedInternalName;
            if (string.IsNullOrWhiteSpace(entry.DetectedVersion))
                entry.DetectedVersion = prior.DetectedVersion;
        }

        return normalized;
    }

    private static bool HasDisplayDetails(DadDependencySnapshot? snapshot)
        => snapshot?.Entries?.Any(static entry =>
            entry != null &&
            (!string.IsNullOrWhiteSpace(entry.DetectedInternalName) ||
             !string.IsNullOrWhiteSpace(entry.DetectedVersion))) == true;

    private static DadDependencyEntry EvaluateCandidate(
        DadDependencyRequirement requirement,
        DadInstalledPluginMetadata plugin)
    {
        if (!Version.TryParse(plugin.Version, out var detectedVersion))
        {
            return CreateEntry(
                requirement,
                DadDependencyState.Checking,
                plugin,
                $"{requirement.DisplayName} reported an unknown version; checking again.");
        }

        var requiresUpdate = plugin.IsOutdated;
        if (!string.IsNullOrWhiteSpace(requirement.MinimumVersion))
        {
            if (!Version.TryParse(requirement.MinimumVersion, out var minimumVersion))
            {
                return CreateEntry(
                    requirement,
                    DadDependencyState.Checking,
                    plugin,
                    $"{requirement.DisplayName} has an invalid minimum-version rule; checking again.");
            }

            requiresUpdate |= detectedVersion < minimumVersion;
        }

        if (requiresUpdate)
        {
            var minimumText = string.IsNullOrWhiteSpace(requirement.MinimumVersion)
                ? string.Empty
                : $" (requires {requirement.MinimumVersion} or newer)";
            return CreateEntry(
                requirement,
                DadDependencyState.UpdateRequired,
                plugin,
                $"{requirement.DisplayName} {plugin.Version} must be updated{minimumText}.");
        }

        if (!plugin.IsLoaded)
        {
            return CreateEntry(
                requirement,
                DadDependencyState.InstalledNotLoaded,
                plugin,
                $"{requirement.DisplayName} is installed but disabled or not loaded.");
        }

        return CreateEntry(
            requirement,
            DadDependencyState.Ready,
            plugin,
            $"{requirement.DisplayName} {plugin.Version} is loaded.");
    }

    private static DadDependencyEntry CreateEntry(
        DadDependencyRequirement requirement,
        DadDependencyState state,
        DadInstalledPluginMetadata? plugin = null,
        string summary = "")
        => new()
        {
            RequirementId = requirement.RequirementId,
            DisplayName = requirement.DisplayName,
            AcceptedInternalNames = [..requirement.AcceptedInternalNames],
            State = state,
            DetectedInternalName = plugin?.InternalName ?? string.Empty,
            DetectedVersion = plugin?.Version ?? string.Empty,
            MinimumVersion = requirement.MinimumVersion,
            OperatorSummary = summary,
        };

    private static int StatusRank(DadDependencyState state)
        => state switch
        {
            DadDependencyState.UpdateRequired => 0,
            DadDependencyState.InstalledNotLoaded => 1,
            DadDependencyState.Checking => 2,
            DadDependencyState.Missing => 3,
            _ => 4,
        };
}

public sealed record DadDependencyGateResult(
    bool Ready,
    DadDependencyState State,
    string Summary,
    long Revision = 0);

public static class DadDependencyGateRules
{
    public static DadDependencyGateResult EvaluateParticipant(
        DadParticipantSnapshot? participant,
        DateTime nowUtc,
        TimeSpan staleAfter,
        string participantLabel = "Selected client")
    {
        if (participant == null)
        {
            return new DadDependencyGateResult(
                false,
                DadDependencyState.Checking,
                $"{participantLabel} is absent. Start that game client manually so DAD can check its required plugins.");
        }

        if (participant.State == DadParticipantState.Stale ||
            participant.LastHeartbeatUtc == default ||
            nowUtc - participant.LastHeartbeatUtc >= staleAfter)
        {
            return new DadDependencyGateResult(
                false,
                DadDependencyState.Checking,
                $"{participantLabel} has stale dependency truth; waiting for a fresh heartbeat.",
                participant.Dependencies.Revision);
        }

        var snapshot = DadDependencyRules.NormalizePeerSnapshot(participant.Dependencies);
        return snapshot.IsReady
            ? new DadDependencyGateResult(true, DadDependencyState.Ready, $"{participantLabel} required plugins are ready.", snapshot.Revision)
            : new DadDependencyGateResult(false, snapshot.AggregateState, $"{participantLabel}: {snapshot.OperatorSummary}", snapshot.Revision);
    }

    public static DadDependencyGateResult EvaluateCrew(
        DadParticipantSnapshot? coordinator,
        IEnumerable<DadParticipantSnapshot?> selectedWorkers,
        DateTime nowUtc,
        TimeSpan staleAfter)
    {
        var results = new List<DadDependencyGateResult>
        {
            EvaluateParticipant(coordinator, nowUtc, staleAfter, "Coordinator"),
        };
        results.AddRange(selectedWorkers.Select((participant, index) =>
            EvaluateParticipant(participant, nowUtc, staleAfter, $"Selected client {index + 1}")));

        var blocker = results.FirstOrDefault(static result => !result.Ready);
        return blocker ?? new DadDependencyGateResult(
            true,
            DadDependencyState.Ready,
            "Every selected client has all required plugins loaded and current.",
            results.Count == 0 ? 0 : results.Max(static result => result.Revision));
    }
}

public static class DadDependencyMutationBoundaryRules
{
    public static bool CanCross(bool runAlreadyApproved, IEnumerable<bool> selectedReadiness)
        => runAlreadyApproved || selectedReadiness.All(static ready => ready);
}

public static class DadDependencyWindowRules
{
    public static bool ShouldBeOpen(bool dadEnabled, DadDependencySnapshot? snapshot)
        => dadEnabled && !DadDependencyRules.NormalizePeerSnapshot(snapshot).IsReady;

    public static bool ResolveCloseAttempt(bool dadEnabled, DadDependencySnapshot? snapshot)
        => ShouldBeOpen(dadEnabled, snapshot);
}

public sealed record DadDependencyInstallerOption(string Label, string SearchText, bool UpdatesOnly, bool InstalledOnly);

public static class DadDependencyInstallerRules
{
    public static IReadOnlyList<DadDependencyInstallerOption> ResolveOptions(DadDependencyEntry entry)
    {
        if (entry.State is not (DadDependencyState.Missing or DadDependencyState.InstalledNotLoaded or DadDependencyState.UpdateRequired))
            return [];

        var updatesOnly = entry.State == DadDependencyState.UpdateRequired;
        var installedOnly = entry.State == DadDependencyState.InstalledNotLoaded;
        if (string.Equals(entry.RequirementId, "BossMod", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("Find BMR (BossModReborn)", "BossModReborn", updatesOnly, installedOnly),
                new("Find VBM (BossMod)", "BossMod", updatesOnly, installedOnly),
            ];
        }

        var search = entry.AcceptedInternalNames.FirstOrDefault() ?? entry.DisplayName;
        return [new("Open Plugin Installer", search, updatesOnly, installedOnly)];
    }
}

public static class DadDebugUiRules
{
    public const string CrewRosterStepId = "crew-roster";
    public const string CrewCharactersStepId = "crew-characters";
    public const string CrewAccountsStepId = "crew-accounts";
    public const string CrewLaunchProfilesStepId = "crew-launch-profiles";
    public const string CrewReviewStepId = "crew-review";

    public static bool ShowLaunchProfiles(bool debugUiEnabled) => debugUiEnabled;

    public static int PresetCrewColumnCount(bool debugUiEnabled) => debugUiEnabled ? 11 : 10;

    public static bool CanRunLaunchProfileDiagnostics(bool debugUiEnabled) => debugUiEnabled;

    public static string ResolveVisibleCrewStep(string? currentStepId, bool debugUiEnabled)
        => !debugUiEnabled && string.Equals(currentStepId, CrewLaunchProfilesStepId, StringComparison.Ordinal)
            ? CrewReviewStepId
            : string.IsNullOrWhiteSpace(currentStepId)
                ? CrewRosterStepId
                : currentStepId;

    public static string FormatWakePolicy(DadSchedulerWakePolicy policy, bool debugUiEnabled)
        => policy == DadSchedulerWakePolicy.LaunchIfOffline && !debugUiEnabled
            ? "Wake/relog"
            : policy.ToString();
}
