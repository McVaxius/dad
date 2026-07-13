namespace dad.Models;

internal enum DadRemoteAssignmentDisposition
{
    Pending,
    Accepted,
    Rejected,
}

internal sealed class DadRemoteAssignmentState
{
    public string RunId { get; init; } = string.Empty;
    public string SlotId { get; init; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; init; } = new(string.Empty);
    public DadRemoteAssignmentDisposition Disposition { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public string Summary { get; set; } = string.Empty;
}

internal sealed class DadRemoteAssignmentTracker
{
    private readonly Dictionary<string, DadRemoteAssignmentState> states = new(StringComparer.OrdinalIgnoreCase);
    private string activeRunId = string.Empty;

    public void BeginAttempt(string runId)
    {
        runId = Normalize(runId);
        if (string.Equals(activeRunId, runId, StringComparison.OrdinalIgnoreCase))
            return;

        states.Clear();
        activeRunId = runId;
    }

    public DadRemoteAssignmentState MarkPending(string runId, DadFrozenRunSlot slot)
    {
        BeginAttempt(runId);
        var state = GetOrCreate(runId, slot);
        if (state.Disposition != DadRemoteAssignmentDisposition.Accepted)
        {
            state.Disposition = DadRemoteAssignmentDisposition.Pending;
            state.Summary = $"{slot.SlotId} assignment acknowledgement is pending from frozen worker '{slot.WorkerSessionId}'.";
        }

        return state;
    }

    public DadRemoteAssignmentState Observe(
        string runId,
        DadFrozenRunSlot slot,
        DadParticipantReadyDto response,
        DateTime observedAtUtc)
    {
        BeginAttempt(runId);
        var state = GetOrCreate(runId, slot);
        if (state.Disposition == DadRemoteAssignmentDisposition.Accepted)
            return state;

        var rejection = Validate(runId, slot, response);
        if (!string.IsNullOrWhiteSpace(rejection))
        {
            state.Disposition = DadRemoteAssignmentDisposition.Rejected;
            state.Summary = rejection;
            return state;
        }

        state.Disposition = DadRemoteAssignmentDisposition.Accepted;
        state.AcceptedAtUtc = EnsureUtc(observedAtUtc);
        state.Summary = $"{slot.SlotId} assignment accepted by frozen worker '{slot.WorkerSessionId}'.";
        return state;
    }

    public bool IsAccepted(string runId, DadFrozenRunSlot slot)
        => states.TryGetValue(BuildKey(runId, slot.SlotId, slot.WorkerSessionId), out var state) &&
           state.Disposition == DadRemoteAssignmentDisposition.Accepted;

    public DadRemoteAssignmentState? Get(string runId, DadFrozenRunSlot slot)
        => states.TryGetValue(BuildKey(runId, slot.SlotId, slot.WorkerSessionId), out var state)
            ? state
            : null;

    public void Clear()
    {
        states.Clear();
        activeRunId = string.Empty;
    }

    private DadRemoteAssignmentState GetOrCreate(string runId, DadFrozenRunSlot slot)
    {
        var key = BuildKey(runId, slot.SlotId, slot.WorkerSessionId);
        if (states.TryGetValue(key, out var state))
            return state;

        state = new DadRemoteAssignmentState
        {
            RunId = Normalize(runId),
            SlotId = Normalize(slot.SlotId),
            WorkerSessionId = slot.WorkerSessionId,
            Disposition = DadRemoteAssignmentDisposition.Pending,
        };
        states[key] = state;
        return state;
    }

    private static string Validate(string runId, DadFrozenRunSlot slot, DadParticipantReadyDto response)
    {
        if (!Same(response.RunId, runId))
            return $"{slot.SlotId} rejected assignment acknowledgement for wrong run '{response.RunId}'.";
        if (!Same(response.WorkerSessionId.Value, slot.WorkerSessionId.Value))
            return $"{slot.SlotId} rejected assignment acknowledgement from wrong worker '{response.WorkerSessionId}'.";
        if (!response.AcceptedAssignment)
            return $"{slot.SlotId} worker '{slot.WorkerSessionId}' rejected the frozen assignment: {response.BlockerSummary}".Trim();

        var snapshot = response.Snapshot;
        if (snapshot == null)
            return $"{slot.SlotId} rejected assignment acknowledgement without a runtime snapshot.";
        if (!Same(snapshot.RunId, runId))
            return $"{slot.SlotId} rejected assignment snapshot for wrong run '{snapshot.RunId}'.";
        if (!Same(snapshot.AssignedSlotId, slot.SlotId))
            return $"{slot.SlotId} rejected assignment snapshot for wrong slot '{snapshot.AssignedSlotId}'.";
        if (!Same(snapshot.WorkerSessionId.Value, slot.WorkerSessionId.Value))
            return $"{slot.SlotId} rejected assignment snapshot from wrong worker '{snapshot.WorkerSessionId}'.";
        if (!Same(snapshot.ManagedAccountKey.Value, slot.AccountKey.Value))
            return $"{slot.SlotId} rejected assignment snapshot for wrong account '{snapshot.ManagedAccountKey}'.";
        if (!Same(response.CharacterKey.Value, slot.CharacterKey.Value) ||
            !Same(snapshot.ActiveCharacterKey.Value, slot.CharacterKey.Value) ||
            !Same(snapshot.Character.CharacterKey, slot.CharacterKey.Value))
        {
            return $"{slot.SlotId} rejected assignment snapshot for wrong character '{snapshot.ActiveCharacterKey}'.";
        }
        if (snapshot.Character.ContentId != slot.ContentId)
            return $"{slot.SlotId} rejected assignment snapshot for wrong Content ID {snapshot.Character.ContentId}.";

        return string.Empty;
    }

    private static string BuildKey(string runId, string slotId, DadWorkerSessionId workerSessionId)
        => $"{Normalize(runId)}|{Normalize(slotId)}|{Normalize(workerSessionId.Value)}";

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
