using dad.Models;

namespace dad.Windows;

/// <summary>Presentation-only destinations exposed by Home, header, Settings, and expert editors.</summary>
public enum DadGuideFlow
{
    Landing,
    Coordinator,
    Client,
    FirstPreset,
    Crew,
    Schedule,
}

internal sealed record DadGuideProgress(
    DadGuideFlow Flow,
    string Title,
    int Complete,
    int Total,
    string NextAction)
{
    public bool Ready => Total > 0 && Complete >= Total;
}

internal static class DadGuideReadiness
{
    public static DadGuideProgress Build(Plugin plugin, DadGuideFlow flow)
    {
        var configuration = plugin.Configuration;
        var profile = plugin.ConfigManager.GetActiveConfig();
        var transport = plugin.TransportService.CurrentTransport;
        var catalog = plugin.RosterCatalogService.CurrentCatalog;
        var runState = plugin.GetVisibleRunState();
        var plannerSnapshot = plugin.GetPlannerUiSnapshot(runState);
        var selectedGroup = plugin.GetSelectedPlannerGroup();

        return flow switch
        {
            DadGuideFlow.Coordinator => Progress(
                flow,
                "Set up a Coordinator",
                [
                    (configuration.PluginEnabled, "Enable DAD."),
                    (profile.Enabled, "Allow DAD to automate this character."),
                    (!string.IsNullOrWhiteSpace(configuration.ClientAccountId), "Select the account owned by this client."),
                    (configuration.RunAsServerDad, "Choose the Coordinator role."),
                    (ValidEndpoint(configuration.ServerListenHost, configuration.ServerListenPort), "Configure a valid listener host and port."),
                    (!transport.SharedSecretRequired || transport.SharedSecretConfigured, "Generate and apply a LAN shared secret."),
                    (plugin.TransportService.IsReady && !string.IsNullOrWhiteSpace(transport.ListenerEndpoint), "Start the Coordinator listener and resolve its first connection blocker."),
                    (Math.Max(transport.PublishedParticipantCount, transport.KnownParticipantCount) > 0, "Connect at least one participant and refresh the crew."),
                ]),
            DadGuideFlow.Client => Progress(
                flow,
                "Connect a Client",
                [
                    (configuration.PluginEnabled, "Enable DAD."),
                    (profile.Enabled, "Allow DAD to automate this character."),
                    (!string.IsNullOrWhiteSpace(configuration.ClientAccountId), "Select the account owned by this client."),
                    (!configuration.RunAsServerDad, "Choose the Client role."),
                    (ValidEndpoint(configuration.ServerDadHost, configuration.ServerDadPort), "Enter the Coordinator endpoint."),
                    (!transport.SharedSecretRequired || transport.SharedSecretConfigured, "Paste and apply the Coordinator's LAN shared secret."),
                    (transport.AuthorityRoutable || plugin.HasServerDadAuthority(), "Verify the endpoint and secret until authenticated authority is discovered."),
                ]),
            DadGuideFlow.FirstPreset => Progress(
                flow,
                "Create a Preset",
                [
                    (configuration.PlannerGroups.Count > 0, "Choose an activity and create a named preset."),
                    (selectedGroup != null, "Select the preset you want to finish."),
                    (selectedGroup != null && selectedGroup.Slots.Any(static slot =>
                        !slot.IsSubstitute && (!slot.RequiredAccountKey.IsEmpty || !slot.RequiredCharacterKey.IsEmpty)), "Assign at least one primary character row."),
                    (selectedGroup?.StopPolicy != null, "Choose a stop rule and finish behavior."),
                    (selectedGroup != null && plannerSnapshot.SchedulerPreview.CanStart, "Resolve the first validation blocker."),
                ]),
            DadGuideFlow.Crew => Progress(
                flow,
                "Build the Crew",
                [
                    (catalog.Characters.Count > 0, "Refresh the local and connected roster."),
                    (catalog.Accounts.Count > 0, "Confirm which account owns each local character."),
                    (catalog.Characters.Any(static row => row.Visibility == DadRosterVisibility.Active) &&
                     !catalog.Characters.Any(static row => row.Visibility == DadRosterVisibility.Active &&
                         (row.AccountKey.IsEmpty || row.IsStale || row.NeedsRosterUpdate)), "Resolve stale or unassigned Active rows."),
                    (configuration.LaunchProfiles.Any(static launch => launch.Enabled && !launch.AccountKey.IsEmpty), "Import, enable, and map a launch profile."),
                ]),
            DadGuideFlow.Schedule => BuildScheduleProgress(plugin),
            _ => new DadGuideProgress(DadGuideFlow.Landing, "DAD Guide", 0, 0, "Choose a guided task."),
        };
    }

    private static DadGuideProgress BuildScheduleProgress(Plugin plugin)
    {
        var snapshot = plugin.SchedulerService.GetScheduleSnapshot();
        var schedule = snapshot.Schedules.FirstOrDefault();
        var knownGroups = plugin.Configuration.PlannerGroups
            .Select(static group => group.GroupId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entriesValid = schedule != null &&
                           schedule.Entries.Count > 0 &&
                           schedule.Entries.All(entry =>
                               !string.IsNullOrWhiteSpace(entry.GroupId) && knownGroups.Contains(entry.GroupId));
        var lastDryRun = schedule == null
            ? null
            : snapshot.RecentResults.FirstOrDefault(result =>
                result.DryRun &&
                string.Equals(result.ScheduleId, schedule.ScheduleId, StringComparison.OrdinalIgnoreCase));

        return Progress(
            DadGuideFlow.Schedule,
            "Build a Schedule",
            [
                (plugin.Configuration.PlannerGroups.Count > 0, "Create at least one saved preset first."),
                (schedule != null, "Create or select a schedule."),
                (schedule?.Entries.Count > 0, "Add presets in the order they should run."),
                (entriesValid, "Replace entries that reference missing presets."),
                (lastDryRun?.Success == true, "Run a successful dry-run for this schedule."),
            ]);
    }

    private static DadGuideProgress Progress(
        DadGuideFlow flow,
        string title,
        IReadOnlyList<(bool Complete, string Next)> checks)
    {
        var complete = checks.Count(static check => check.Complete);
        var next = checks.FirstOrDefault(static check => !check.Complete).Next;
        return new DadGuideProgress(
            flow,
            title,
            complete,
            checks.Count,
            string.IsNullOrWhiteSpace(next) ? "Review the completed workflow." : next);
    }

    private static bool ValidEndpoint(string host, int port)
        => !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
}
