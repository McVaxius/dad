namespace dad.Models;

internal enum DadInviteTestMode { Mixed, Cross, Serial, Reverse }

// Runtime-only. The existing coordinator owns authentication, dispatch and cleanup.
internal sealed class DadInviteDiagnostics
{
    internal const int Capacity = 256;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private readonly Queue<string> events = new();
    private readonly HashSet<ulong> joined = [];
    private ulong[] targets = [];
    private readonly Dictionary<string, string> observations = [];
    internal string RunId { get; private set; } = string.Empty;
    internal DadInviteTestMode Mode { get; private set; }
    internal bool Active { get; private set; }
    internal DateTime StartedAtUtc { get; private set; }
    internal DateTime DeadlineUtc => StartedAtUtc + Timeout;
    internal string Summary { get; private set; } = "No invite comparison started.";

    internal static void PrepareRuntimeCrew(DadPlannerGroup group,
        DadPlannerRunRequestPreview preview, DadActivityPreset alliancePreview)
    {
        if (!group.AutoPartyFormationOnly || preview.Request?.Orchestration.AutoPartyFormationOnly != true)
            throw new InvalidOperationException("Invite comparison preparation requires runtime formation-only copies.");
        foreach (var slot in group.Slots)
        {
            slot.RequiredJobId = null;
            slot.WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly;
            slot.CharacterLoadInstruction = new();
        }
        // Scheduler binding consumes resolved preview rows, not just the group rows.
        foreach (var slot in preview.PlannerPreview.SelectedCharacters) slot.RequiredJobId = null;
        foreach (var slot in alliancePreview.SelectedCharacters) slot.RequiredJobId = null;
        foreach (var row in preview.Request.Orchestration.RequiredRosterCharacters) row.RequiredJobId = null;
        foreach (var row in preview.Request.Orchestration.PreferredRosterCharacters) row.RequiredJobId = null;
        preview.Request.RefreshRecommendedGear = false;
        preview.Request.ApplyOrchestrationDefaults();
    }

    internal void Start(string runId, DadInviteTestMode mode, IEnumerable<ulong> orderedTargets, DateTime now)
    {
        var frozen = orderedTargets.ToArray();
        if (string.IsNullOrWhiteSpace(runId) || frozen.Length is < 1 or > 7 ||
            frozen.Any(id => id == 0) || frozen.Distinct().Count() != frozen.Length)
            throw new ArgumentException("Invite test requires an exact request and distinct nonzero target identities.");
        if (Active) throw new InvalidOperationException("An invite comparison is already active.");
        RunId = runId;
        Mode = mode;
        targets = mode == DadInviteTestMode.Reverse ? frozen.Reverse().ToArray() : frozen;
        joined.Clear();
        events.Clear();
        observations.Clear();
        StartedAtUtc = now;
        Active = true;
        Summary = $"Invite test {mode}: preparing {targets.Length} recipient(s); request={runId}.";
        Record(now, $"start request={runId} mode={mode} recipients={targets.Length} timeoutSeconds={Timeout.TotalSeconds:0}");
    }

    internal bool Allows(string runId, ulong target, IReadOnlyCollection<ulong> members,
        DateTime now, bool debugEnabled, out DadNativePartyInviteType? forcedType)
    {
        forcedType = null;
        if (!string.Equals(RunId, runId, StringComparison.Ordinal)) return true;
        if (!Active) return false; // Keep this request revoked while cancellation is settling.
        if (!debugEnabled || now >= DeadlineUtc)
        {
            Finish(now, debugEnabled ? "Timed out after 120 seconds." : "Debug mode disabled.");
            return false;
        }
        ObserveMembers(members, now);
        if (!targets.Contains(target)) return false;
        if (Mode is DadInviteTestMode.Serial or DadInviteTestMode.Reverse &&
            targets.FirstOrDefault(id => !members.Contains(id)) != target)
            return false;
        if (Mode != DadInviteTestMode.Mixed) forcedType = DadNativePartyInviteType.CrossWorldContentId;
        return true;
    }

    internal void ObserveMembers(IReadOnlyCollection<ulong> members, DateTime now)
    {
        if (!Active) return;
        foreach (var id in targets.Where(members.Contains))
            if (joined.Add(id)) Record(now, $"membership-observed recipient={Array.IndexOf(targets, id) + 1} elapsedSeconds={(now - StartedAtUtc).TotalSeconds:F3}");
    }

    internal void Observe(DateTime now, string observation, string channel = "receiver")
    {
        if (observations.TryGetValue(channel, out var last) && string.Equals(last, observation, StringComparison.Ordinal)) return;
        observations[channel] = observation;
        Record(now, observation);
    }

    internal void Record(DateTime now, string message)
    {
        while (events.Count >= Capacity) events.Dequeue();
        events.Enqueue($"{now:O} {message}");
    }

    internal void Finish(DateTime now, string reason)
    {
        if (!Active) return;
        Active = false;
        Summary = $"Invite test {Mode}: {reason} Joined={joined.Count}/{targets.Length}; elapsed={(now - StartedAtUtc).TotalSeconds:F1}s; request={RunId}.";
        Record(now, Summary);
    }

    internal string Report() => Summary + "\n" + string.Join("\n", events);
}
