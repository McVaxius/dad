namespace dad.Models;

public readonly record struct DadSchedulerClientRoute(
    string SlotId,
    DadParticipantSnapshot Participant);

public readonly record struct DadWakeSlotPipelineDecision(
    bool CanDispatch,
    DadWakeTakeoverMessageKind MessageKind,
    DadWakeCommitKind CommitKind,
    DateTime? ExecutionTimeUtc,
    string Summary);

public static class DadSchedulerRoutingRules
{
    public static bool IsTakeoverCancellationComplete(DadWakeTakeoverResultDto? result)
        => result is
        {
            Phase: DadWakeTakeoverPhase.Cancelled,
            AcknowledgementState: DadWakeAcknowledgementState.Executed,
        } or
        {
            Phase: DadWakeTakeoverPhase.Ready,
        };

    public static bool RequiresTakeoverCancellation(DadSchedulerSlotState slot)
        => slot.WakePolicy == DadSchedulerWakePolicy.LaunchIfOffline &&
           !string.IsNullOrWhiteSpace(slot.OperationToken) &&
           slot.TakeoverPhase != DadWakeTakeoverPhase.Ready &&
           !(slot.TakeoverPhase == DadWakeTakeoverPhase.Cancelled &&
             slot.AcknowledgementState == DadWakeAcknowledgementState.Executed);

    public static DadWorkerSessionId PreserveFrozenWorkerSession(
        DadWorkerSessionId frozenWorkerSessionId,
        DadWorkerSessionId candidateWorkerSessionId)
        => frozenWorkerSessionId.IsEmpty ? candidateWorkerSessionId : frozenWorkerSessionId;

    public static bool HasLatestSafeTakeoverProjection(DadSchedulerSlotState slot)
        => slot.ClientConnected &&
           slot.BasePostArReady &&
           slot.AutoRetainerAvailable &&
           !slot.AutoRetainerBusy &&
           !slot.ExternalAutomationHeld;

    public static bool IsExactFrozenTakeoverSnapshot(
        DadSchedulerSlotState slot,
        DadParticipantSnapshot snapshot)
        => snapshot != null &&
           snapshot.IsAvailable &&
           (slot.MatchedWorkerSessionId.IsEmpty || string.Equals(
               slot.MatchedWorkerSessionId.Value,
               snapshot.WorkerSessionId.Value,
               StringComparison.OrdinalIgnoreCase)) &&
           !slot.RequiredAccountKey.IsEmpty &&
           string.Equals(
               slot.RequiredAccountKey.Value,
               snapshot.ManagedAccountKey.Value,
               StringComparison.OrdinalIgnoreCase) &&
           !slot.RequiredCharacterKey.IsEmpty &&
           string.Equals(
               slot.RequiredCharacterKey.Value,
               snapshot.ActiveCharacterKey.Value,
               StringComparison.OrdinalIgnoreCase);

    public static bool CanAcceptReadyAcknowledgement(
        DadSchedulerSlotState slot,
        DadWakeTakeoverResultDto result)
        => result.Status == DadWakeTakeoverStatus.Ready &&
           result.Phase == DadWakeTakeoverPhase.Ready &&
           IsExactFrozenTakeoverSnapshot(slot, result.Snapshot) &&
           result.Snapshot.WorldReadyStable &&
           result.AutoRetainerAvailable &&
           !result.AutoRetainerBusy &&
           !result.MultiModeEnabled &&
           !result.ExternalAutomationHeld;

    public static DadAccountKey ResolveStableClientAccount(string configuredClientAccountId)
        => new((configuredClientAccountId ?? string.Empty).Trim());

    public static DadParticipantSnapshot? ResolveExactConnectedClient(
        DadAccountKey requiredAccountKey,
        IEnumerable<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        if (requiredAccountKey.IsEmpty)
            return null;

        var matches = participants.Where(participant =>
            !participant.WorkerSessionId.IsEmpty &&
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.ManagedAccountKey.Value,
                requiredAccountKey.Value,
                StringComparison.OrdinalIgnoreCase) &&
            isWorkerOnline(participant.WorkerSessionId))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static DadParticipantSnapshot? ResolveFrozenConnectedClient(
        DadAccountKey requiredAccountKey,
        DadWorkerSessionId frozenWorkerSessionId,
        IEnumerable<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        if (requiredAccountKey.IsEmpty || frozenWorkerSessionId.IsEmpty)
            return null;

        return participants.FirstOrDefault(participant =>
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.WorkerSessionId.Value,
                frozenWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                participant.ManagedAccountKey.Value,
                requiredAccountKey.Value,
                StringComparison.OrdinalIgnoreCase) &&
            isWorkerOnline(participant.WorkerSessionId));
    }

    public static DadParticipantSnapshot? ResolveCurrentOrSoleReconnectedClient(
        DadAccountKey requiredAccountKey,
        DadWorkerSessionId frozenWorkerSessionId,
        IEnumerable<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        var participantList = participants.ToList();
        var frozen = ResolveFrozenConnectedClient(
            requiredAccountKey,
            frozenWorkerSessionId,
            participantList,
            isWorkerOnline);
        if (frozen != null)
            return frozen;

        // An online exact old session reporting another account is a safety
        // contradiction, not permission to route around it. Rebinding is only
        // available once that old session is absent and one sole exact-account
        // replacement route exists.
        var oldSessionStillOnline = !frozenWorkerSessionId.IsEmpty && participantList.Any(participant =>
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.WorkerSessionId.Value,
                frozenWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) &&
            isWorkerOnline(participant.WorkerSessionId));
        return oldSessionStillOnline
            ? null
            : ResolveExactConnectedClient(requiredAccountKey, participantList, isWorkerOnline);
    }

    public static DadParticipantSnapshot? ResolveFrozenCancellationClient(
        DadWorkerSessionId frozenWorkerSessionId,
        IEnumerable<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline)
    {
        if (frozenWorkerSessionId.IsEmpty)
            return null;

        // Cancellation is cleanup for an operation already accepted by this exact worker
        // session. Account and character projections may legitimately drift while reset/relog
        // cleanup is still pending, but cleanup authority must never move to another session.
        return participants.FirstOrDefault(participant =>
            participant.State != DadParticipantState.Stale &&
            string.Equals(
                participant.WorkerSessionId.Value,
                frozenWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase) &&
            isWorkerOnline(participant.WorkerSessionId));
    }

    public static DadWakeSlotPipelineDecision ResolveNextTakeoverAction(
        DadSchedulerSlotState slot,
        DateTime nowUtc)
    {
        if (!slot.ClientConnected)
        {
            return new DadWakeSlotPipelineDecision(
                false,
                DadWakeTakeoverMessageKind.Status,
                DadWakeCommitKind.None,
                null,
                "Waiting for the frozen worker session.");
        }

        if (slot.TakeoverPhase == DadWakeTakeoverPhase.Prepared)
        {
            if (!HasLatestSafeTakeoverProjection(slot))
            {
                return new DadWakeSlotPipelineDecision(
                    true,
                    DadWakeTakeoverMessageKind.Status,
                    DadWakeCommitKind.None,
                    null,
                    "Latest heartbeat is unsafe; poll without sending reset GO.");
            }

            var execution = slot.ResetExecutionUtc ?? EnsureUtc(nowUtc).AddSeconds(5);
            return new DadWakeSlotPipelineDecision(
                true,
                DadWakeTakeoverMessageKind.Go,
                DadWakeCommitKind.Reset,
                execution,
                "This slot is prepared and can schedule its reset independently.");
        }

        if (slot.TakeoverPhase == DadWakeTakeoverPhase.ResetVerified)
        {
            if (!HasLatestSafeTakeoverProjection(slot))
            {
                return new DadWakeSlotPipelineDecision(
                    true,
                    DadWakeTakeoverMessageKind.Status,
                    DadWakeCommitKind.None,
                    null,
                    "Latest heartbeat is unsafe; poll without sending relog GO.");
            }

            var execution = slot.RelogExecutionUtc ?? EnsureUtc(nowUtc).AddSeconds(5);
            return new DadWakeSlotPipelineDecision(
                true,
                DadWakeTakeoverMessageKind.Go,
                DadWakeCommitKind.Relog,
                execution,
                "This slot verified reset and can schedule its relog independently.");
        }

        if (slot.TakeoverPhase is DadWakeTakeoverPhase.Ready or DadWakeTakeoverPhase.Blocked or DadWakeTakeoverPhase.Cancelled)
        {
            return new DadWakeSlotPipelineDecision(
                false,
                DadWakeTakeoverMessageKind.Status,
                DadWakeCommitKind.None,
                null,
                "This slot is terminal.");
        }

        return new DadWakeSlotPipelineDecision(
            true,
            slot.TakeoverPhase < DadWakeTakeoverPhase.Prepared
                ? DadWakeTakeoverMessageKind.Prepare
                : DadWakeTakeoverMessageKind.Status,
            DadWakeCommitKind.None,
            null,
            "Prepare or poll this slot without waiting for another slot.");
    }

    public static bool TryResolveAllTakeoverClients(
        IReadOnlyList<DadSchedulerSlotState> slots,
        IReadOnlyList<DadParticipantSnapshot> participants,
        Func<DadWorkerSessionId, bool> isWorkerOnline,
        out IReadOnlyList<DadSchedulerClientRoute> routes)
    {
        var resolved = new List<DadSchedulerClientRoute>(slots.Count);
        foreach (var slot in slots)
        {
            var participant = ResolveExactConnectedClient(
                slot.RequiredAccountKey,
                participants,
                isWorkerOnline);
            if (participant == null)
            {
                routes = [];
                return false;
            }

            resolved.Add(new DadSchedulerClientRoute(slot.SlotId, participant));
        }

        routes = resolved;
        return true;
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
