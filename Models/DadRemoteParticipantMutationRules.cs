namespace dad.Models;

internal static class DadRemoteParticipantMutationRules
{
    public static bool TryApplyIdentityValidRuntimeState(
        DadParticipantSnapshot target,
        DadParticipantSnapshot source,
        DadFrozenRunSlot frozenSlot,
        string runId,
        out string blocker)
    {
        blocker = ValidateIdentity(target, source, frozenSlot, runId);
        if (!string.IsNullOrWhiteSpace(blocker))
            return false;

        target.State = source.State;
        target.ClaimState = source.ClaimState;
        target.LeaseState = source.LeaseState;
        target.CancellationState = source.CancellationState;
        target.IsAvailable = source.IsAvailable;
        target.IsEligibleForRun = source.IsEligibleForRun;
        target.PostArReady = source.PostArReady;
        target.WorldReadyStable = source.WorldReadyStable;
        target.AutoRetainerAvailable = source.AutoRetainerAvailable;
        target.AutoRetainerBusy = source.AutoRetainerBusy;
        target.AutoRetainerMultiModeEnabled = source.AutoRetainerMultiModeEnabled;
        target.ExternalAutomationHeld = source.ExternalAutomationHeld;
        target.ExternalAutomationActivity = source.ExternalAutomationActivity;
        target.ExternalAutomationState = source.ExternalAutomationState;
        target.ExternalAutomationSummary = source.ExternalAutomationSummary;
        target.Dependencies = source.Dependencies.Clone();
        target.LastHeartbeatUtc = source.LastHeartbeatUtc;
        target.Character.CurrentJobId = source.Character.CurrentJobId;
        target.Character.CurrentJobAbbrev = source.Character.CurrentJobAbbrev;
        target.Character.CurrentLevel = source.Character.CurrentLevel;
        target.RequestedJobPreparation = source.RequestedJobPreparation?.Clone();
        target.LeaseIssuedUtc = source.LeaseIssuedUtc;
        target.LeaseRenewedUtc = source.LeaseRenewedUtc;
        target.LeaseExpiresUtc = source.LeaseExpiresUtc;
        target.Warnings = [..source.Warnings];
        target.StatusText = source.StatusText;

        // A remote snapshot is local only from the sender's perspective. The coordinator owns
        // locality and authority, and an identity-valid response cannot promote a remote row.
        target.IsLocalClient = false;
        return true;
    }

    private static string ValidateIdentity(
        DadParticipantSnapshot target,
        DadParticipantSnapshot source,
        DadFrozenRunSlot slot,
        string runId)
    {
        if (!Same(target.WorkerSessionId.Value, slot.WorkerSessionId.Value) ||
            !Same(target.AssignedSlotId, slot.SlotId) ||
            !Same(target.ManagedAccountKey.Value, slot.AccountKey.Value) ||
            !Same(target.ActiveCharacterKey.Value, slot.CharacterKey.Value) ||
            !Same(target.Character.CharacterKey, slot.CharacterKey.Value) ||
            target.Character.ContentId != slot.ContentId)
        {
            return $"Coordinator-owned target identity no longer matches frozen {slot.SlotId}.";
        }

        if (!Same(source.RunId, runId))
            return $"Remote response for {slot.SlotId} belongs to wrong run '{source.RunId}'.";
        if (!Same(source.WorkerSessionId.Value, slot.WorkerSessionId.Value))
            return $"Remote response for {slot.SlotId} came from wrong worker '{source.WorkerSessionId}'.";
        if (!Same(source.AssignedSlotId, slot.SlotId))
            return $"Remote response for {slot.SlotId} reports wrong slot '{source.AssignedSlotId}'.";
        if (!Same(source.ManagedAccountKey.Value, slot.AccountKey.Value))
            return $"Remote response for {slot.SlotId} reports wrong account '{source.ManagedAccountKey}'.";
        if (!Same(source.ActiveCharacterKey.Value, slot.CharacterKey.Value) ||
            !Same(source.Character.CharacterKey, slot.CharacterKey.Value))
        {
            return $"Remote response for {slot.SlotId} reports wrong character '{source.ActiveCharacterKey}'.";
        }
        if (source.Character.ContentId != slot.ContentId)
            return $"Remote response for {slot.SlotId} reports wrong Content ID {source.Character.ContentId}.";

        return string.Empty;
    }

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}
