using dad.Models;

namespace dad.Services;

internal enum DadParticipantFrenRiderHandoffStatus
{
    NotRequired,
    WaitingForExactDuty,
    Configured,
    AlreadyConfigured,
    PendingRetry,
    Failed,
}

internal static class DadParticipantFrenRiderTargetRules
{
    public static bool TryResolve(
        DadWorkerExecutionCommand command,
        out string nameAtWorld,
        out string blocker)
    {
        nameAtWorld = string.Empty;
        blocker = string.Empty;

        if (command == null || command.Plan == null || command.Participants == null)
            return Fail("Participant FrenRider handoff is missing its worker command, plan, or frozen assignments.", out blocker);

        if (command.Role != DadWorkerExecutionRole.Participant)
            return Fail("Only a participant worker may configure a FrenRider follow target.", out blocker);

        if (string.IsNullOrWhiteSpace(command.RunId) ||
            !string.Equals(command.RunId.Trim(), command.Plan.Request?.RequestId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Participant FrenRider handoff run id does not match the frozen plan request.", out blocker);
        }

        if (!DadRunSlotManifestRules.TryCreate(command.Plan, out var manifest, out blocker))
            return false;

        var frozenSlot1Rows = manifest.Slots
            .Where(static slot => Same(slot.SlotId, DadPlannerSlotRules.LeaderSlotId))
            .ToList();
        if (frozenSlot1Rows.Count != 1 || !frozenSlot1Rows[0].IsLeader)
            return Fail("Participant FrenRider handoff requires exactly one frozen leader Slot1.", out blocker);

        var slot1Rows = command.Participants
            .Where(static participant => Same(participant.AssignedSlotId, DadPlannerSlotRules.LeaderSlotId))
            .ToList();
        if (slot1Rows.Count != 1)
        {
            return Fail(
                $"Participant FrenRider handoff requires exactly one Slot1 assignment row; found {slot1Rows.Count}.",
                out blocker);
        }

        var localRows = command.Participants.Where(static participant => participant.IsLocalClient).ToList();
        if (localRows.Count != 1)
        {
            return Fail(
                $"Participant FrenRider handoff requires exactly one local assignment row; found {localRows.Count}.",
                out blocker);
        }

        var target = slot1Rows[0];
        var local = localRows[0];
        var frozenSlot1 = frozenSlot1Rows[0];
        if (target.IsLocalClient)
            return Fail("Participant FrenRider Slot1 target must be remote.", out blocker);
        if (!target.IsAuthority)
            return Fail("Participant FrenRider Slot1 target must be authoritative.", out blocker);
        if (target.WorkerSessionId.IsEmpty)
            return Fail("Participant FrenRider Slot1 target is missing its frozen worker session.", out blocker);
        if (local.WorkerSessionId.IsEmpty || Same(target.WorkerSessionId.Value, local.WorkerSessionId.Value))
            return Fail("Participant FrenRider Slot1 target worker session is missing or belongs to the local participant.", out blocker);

        var duplicateTargetSession = command.Participants.Count(participant =>
            Same(participant.WorkerSessionId.Value, target.WorkerSessionId.Value));
        if (duplicateTargetSession != 1)
            return Fail("Participant FrenRider Slot1 target worker session is duplicated.", out blocker);

        if (!Same(target.RunId, command.RunId))
            return Fail($"Participant FrenRider Slot1 target belongs to wrong run '{target.RunId}'.", out blocker);
        if (!Same(target.ManagedAccountKey.Value, frozenSlot1.AccountKey.Value))
            return Fail($"Participant FrenRider Slot1 target reports wrong account '{target.ManagedAccountKey}'.", out blocker);
        if (target.Character == null)
            return Fail("Participant FrenRider Slot1 target is missing its character identity payload.", out blocker);
        if (!Same(target.Character.AccountId, frozenSlot1.AccountKey.Value))
            return Fail($"Participant FrenRider Slot1 target character reports wrong account '{target.Character.AccountId}'.", out blocker);
        if (!Same(target.ActiveCharacterKey.Value, frozenSlot1.CharacterKey.Value) ||
            !Same(target.Character.CharacterKey, frozenSlot1.CharacterKey.Value))
        {
            return Fail($"Participant FrenRider Slot1 target reports wrong character '{target.ActiveCharacterKey}'.", out blocker);
        }
        if (target.Character.ContentId != frozenSlot1.ContentId)
            return Fail($"Participant FrenRider Slot1 target reports wrong Content ID {target.Character.ContentId}.", out blocker);

        var exactTarget = target.ActiveCharacterKey.Value ?? string.Empty;
        if (!IsExactNameAtWorld(exactTarget))
            return Fail("Participant FrenRider Slot1 target is not an exact Name@World character key.", out blocker);

        nameAtWorld = exactTarget;
        return true;
    }

    private static bool IsExactNameAtWorld(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
            return false;

        var separator = value.IndexOf('@');
        if (separator <= 0
            || separator != value.LastIndexOf('@')
            || separator >= value.Length - 1)
        {
            return false;
        }

        var characterName = value[..separator];
        var worldName = value[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(characterName)
               && !string.IsNullOrWhiteSpace(worldName)
               && string.Equals(characterName, characterName.Trim(), StringComparison.Ordinal)
               && string.Equals(worldName, worldName.Trim(), StringComparison.Ordinal)
               && !worldName.Any(char.IsWhiteSpace);
    }

    private static bool Same(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();

    private static bool Fail(string reason, out string blocker)
    {
        blocker = reason;
        return false;
    }
}

internal sealed class DadParticipantFrenRiderHandoffGate
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(5);

    private string targetNameAtWorld = string.Empty;
    private DateTime firstAttemptUtc = DateTime.MinValue;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private string lastSummary = string.Empty;
    private int attemptCount;
    private bool configured;
    private bool failed;

    public DadParticipantFrenRiderHandoffStatus Apply(
        DadWorkerExecutionCommand command,
        bool useFrenRider,
        bool exactRequestedDutyEntered,
        DateTime now,
        Func<string, DadFrenRiderCommandResult> configureAndEnable,
        out string summary)
    {
        if (!useFrenRider)
        {
            summary = "Participant combat-rotation mode does not configure FrenRider.";
            return DadParticipantFrenRiderHandoffStatus.NotRequired;
        }

        if (configured)
        {
            summary = lastSummary;
            return DadParticipantFrenRiderHandoffStatus.AlreadyConfigured;
        }

        if (failed)
        {
            summary = lastSummary;
            return DadParticipantFrenRiderHandoffStatus.Failed;
        }

        if (!exactRequestedDutyEntered)
        {
            summary = "Participant is waiting for exact requested-duty entry before configuring FrenRider.";
            return DadParticipantFrenRiderHandoffStatus.WaitingForExactDuty;
        }

        if (string.IsNullOrEmpty(targetNameAtWorld) &&
            !DadParticipantFrenRiderTargetRules.TryResolve(command, out targetNameAtWorld, out var blocker))
        {
            failed = true;
            summary = $"Participant FrenRider handoff rejected the frozen Slot1 target: {blocker}";
            lastSummary = summary;
            return DadParticipantFrenRiderHandoffStatus.Failed;
        }

        if (attemptCount > 0 && now < nextAttemptUtc)
        {
            summary = lastSummary;
            return DadParticipantFrenRiderHandoffStatus.PendingRetry;
        }

        if (attemptCount == 0)
            firstAttemptUtc = now;

        attemptCount++;
        DadFrenRiderCommandResult result;
        try
        {
            result = configureAndEnable(targetNameAtWorld);
        }
        catch (Exception ex)
        {
            result = DadFrenRiderCommandResult.Failure(
                $"FrenRider.Dad.ConfigureAndEnable threw {ex.GetType().Name}: {ex.Message}");
        }

        if (result.Succeeded)
        {
            configured = true;
            summary = $"Configured and enabled FrenRider to follow frozen Slot1 '{targetNameAtWorld}'.";
            lastSummary = summary;
            return DadParticipantFrenRiderHandoffStatus.Configured;
        }

        var failure = string.IsNullOrWhiteSpace(result.FailureReason)
            ? "FrenRider rejected the request"
            : result.FailureReason;
        if (now - firstAttemptUtc >= RetryWindow)
        {
            failed = true;
            summary = $"Participant FrenRider handoff failed after five seconds for frozen Slot1 '{targetNameAtWorld}': {failure}.";
            lastSummary = summary;
            return DadParticipantFrenRiderHandoffStatus.Failed;
        }

        nextAttemptUtc = now + RetryInterval;
        summary = $"Participant FrenRider handoff for frozen Slot1 '{targetNameAtWorld}' was rejected: {failure}. Retrying once per second for five seconds.";
        lastSummary = summary;
        return DadParticipantFrenRiderHandoffStatus.PendingRetry;
    }

    public void Reset()
    {
        targetNameAtWorld = string.Empty;
        firstAttemptUtc = DateTime.MinValue;
        nextAttemptUtc = DateTime.MinValue;
        lastSummary = string.Empty;
        attemptCount = 0;
        configured = false;
        failed = false;
    }
}
