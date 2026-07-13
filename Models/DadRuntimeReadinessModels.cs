namespace dad.Models;

public static class DadParticipantWorldSafetyRules
{
    public static bool IsWorldReadyStable(
        bool isLoggedIn,
        bool hasLocalPlayer,
        string? activeCharacterKey,
        ulong contentId,
        DadReadinessState readiness,
        bool unsafeConditionActive)
        => isLoggedIn &&
           hasLocalPlayer &&
           !string.IsNullOrWhiteSpace(activeCharacterKey) &&
           contentId != 0 &&
           readiness == DadReadinessState.Ready &&
           !unsafeConditionActive;
}

/// <summary>
/// Semantic runtime truth that can change whether a scheduler slot may advance.
/// Display text, warnings, and observation timestamps are intentionally excluded.
/// </summary>
public readonly record struct DadRuntimeReadinessSignature
{
    public string ManagedAccountKey { get; init; }
    public string ActiveCharacterKey { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsEligibleForRun { get; init; }
    public DadParticipantState ParticipantState { get; init; }
    public bool PostArReady { get; init; }
    public bool WorldReadyStable { get; init; }
    public bool AutoRetainerAvailable { get; init; }
    public bool AutoRetainerBusy { get; init; }
    public bool AutoRetainerMultiModeEnabled { get; init; }
    public bool SuppressionReadable { get; init; }
    public bool AutoRetainerSuppressed { get; init; }
    public bool DadOwnsSuppression { get; init; }
    public bool DadOwnsCharacterPostprocess { get; init; }
    public bool ExternalAutomationHeld { get; init; }
    public string ExternalAutomationActivity { get; init; }
    public string ExternalAutomationState { get; init; }
    public bool HasTakeover { get; init; }
    public string TakeoverOperationToken { get; init; }
    public DadWakeTakeoverStatus TakeoverStatus { get; init; }
    public DadWakeTakeoverStage TakeoverStage { get; init; }
    public DadWakeTakeoverPhase TakeoverPhase { get; init; }
    public DadWakeCommitKind TakeoverCommitKind { get; init; }
    public DadWakeAcknowledgementState TakeoverAcknowledgement { get; init; }
    public DadVermaxionReservationState VermaxionReservationState { get; init; }
    public string RequestedJobPreparationKey { get; init; }
    public DadRequestedJobPreparationStatus RequestedJobPreparationStatus { get; init; }
    public uint RequestedJobCurrentJobId { get; init; }

    public static DadRuntimeReadinessSignature Create(
        DadParticipantSnapshot? participant,
        bool suppressionReadable = false,
        bool autoRetainerSuppressed = false,
        bool dadOwnsSuppression = false,
        bool dadOwnsCharacterPostprocess = false,
        DadWakeTakeoverResultDto? takeover = null)
    {
        participant ??= new DadParticipantSnapshot();
        return new DadRuntimeReadinessSignature
        {
            ManagedAccountKey = Normalize(participant.ManagedAccountKey.Value),
            ActiveCharacterKey = Normalize(participant.ActiveCharacterKey.Value),
            IsAvailable = participant.IsAvailable,
            IsEligibleForRun = participant.IsEligibleForRun,
            ParticipantState = participant.State,
            PostArReady = participant.PostArReady,
            WorldReadyStable = participant.WorldReadyStable,
            AutoRetainerAvailable = participant.AutoRetainerAvailable,
            AutoRetainerBusy = participant.AutoRetainerBusy,
            AutoRetainerMultiModeEnabled = participant.AutoRetainerMultiModeEnabled,
            SuppressionReadable = suppressionReadable,
            AutoRetainerSuppressed = autoRetainerSuppressed,
            DadOwnsSuppression = dadOwnsSuppression,
            DadOwnsCharacterPostprocess = dadOwnsCharacterPostprocess,
            ExternalAutomationHeld = participant.ExternalAutomationHeld,
            ExternalAutomationActivity = Normalize(participant.ExternalAutomationActivity),
            ExternalAutomationState = Normalize(participant.ExternalAutomationState),
            HasTakeover = takeover != null,
            TakeoverOperationToken = Normalize(takeover?.OperationToken),
            TakeoverStatus = takeover?.Status ?? DadWakeTakeoverStatus.Pending,
            TakeoverStage = takeover?.Stage ?? DadWakeTakeoverStage.None,
            TakeoverPhase = takeover?.Phase ?? DadWakeTakeoverPhase.AwaitingArHook,
            TakeoverCommitKind = takeover?.CommitKind ?? DadWakeCommitKind.None,
            TakeoverAcknowledgement = takeover?.AcknowledgementState ?? DadWakeAcknowledgementState.Pending,
            VermaxionReservationState = takeover?.VermaxionReservationState ?? DadVermaxionReservationState.NotLoaded,
            RequestedJobPreparationKey = BuildRequestedJobPreparationKey(participant.RequestedJobPreparation),
            RequestedJobPreparationStatus = participant.RequestedJobPreparation?.Status ?? DadRequestedJobPreparationStatus.NotRequested,
            RequestedJobCurrentJobId = participant.Character.CurrentJobId.GetValueOrDefault(),
        };
    }

    private static string BuildRequestedJobPreparationKey(DadRequestedJobPreparationProof? proof)
    {
        if (proof == null)
            return string.Empty;

        var key = proof.Key;
        return string.Join(
            "|",
            Normalize(key.RunId),
            Normalize(key.WorkerSessionId.Value),
            Normalize(key.SlotId),
            Normalize(key.AccountKey.Value),
            Normalize(key.CharacterKey.Value),
            key.ContentId,
            key.RequiredJobId.GetValueOrDefault());
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}

public sealed class DadRuntimeReadinessTracker
{
    private readonly object gate = new();
    private DadRuntimeReadinessSignature current;
    private bool initialized;
    private long revision;

    public long Revision
    {
        get
        {
            lock (gate)
                return revision;
        }
    }

    public bool WouldChange(DadRuntimeReadinessSignature next)
    {
        lock (gate)
            return initialized && current != next;
    }

    public bool Observe(DadRuntimeReadinessSignature next, out long observedRevision)
    {
        lock (gate)
        {
            if (!initialized)
            {
                current = next;
                initialized = true;
                observedRevision = revision;
                return false;
            }

            if (current == next)
            {
                observedRevision = revision;
                return false;
            }

            current = next;
            observedRevision = ++revision;
            return true;
        }
    }
}

public static class DadSchedulerRuntimeWakeRules
{
    public static DadSchedulerPresetPhase ResolveInitialPhase(
        bool plannerCanStart,
        bool plannerCanSchedule,
        bool slotsReadyToStart)
        => !plannerCanStart && plannerCanSchedule
            ? DadSchedulerPresetPhase.Resolving
            : slotsReadyToStart
                ? DadSchedulerPresetPhase.ReadyToStart
                : DadSchedulerPresetPhase.Resolving;

    public static int MakeMatchingTakeoverChecksDue(
        IEnumerable<DadSchedulerSlotState> slots,
        DadWorkerSessionId workerSessionId)
    {
        if (workerSessionId.IsEmpty)
            return 0;

        var changed = 0;
        foreach (var slot in slots)
        {
            if (!string.Equals(
                    slot.MatchedWorkerSessionId.Value,
                    workerSessionId.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            slot.NextTakeoverStatusCheckUtc = DateTime.MinValue;
            changed++;
        }

        return changed;
    }
}
