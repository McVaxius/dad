using dad.Models;

namespace dad.Services;

internal static class DadWorkerCommandValidationRules
{
    public static bool TryValidate(
        DadWorkerExecutionCommand command,
        DadParticipantSnapshot localRuntime,
        out DadParticipantSnapshot localAssignment,
        out string blocker)
    {
        localAssignment = new DadParticipantSnapshot();
        blocker = string.Empty;

        if (command == null || command.Plan == null || command.Plan.Request == null || command.Plan.Modules == null)
            return Fail("Worker command is missing its plan, request, or modules.", out blocker);

        if (command.SchemaVersion != 1)
            return Fail($"Unsupported worker command schema {command.SchemaVersion}.", out blocker);

        if (string.IsNullOrWhiteSpace(command.RunId) ||
            !string.Equals(command.RunId, command.Plan.Request.RequestId, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Worker command run id does not match its plan request.", out blocker);
        }

        if (command.ModuleIndex < 0 || command.ModuleIndex >= command.Plan.Modules.Count)
            return Fail($"Worker command module index {command.ModuleIndex} is invalid.", out blocker);

        if (!DadRunSlotManifestRules.TryCreate(command.Plan, out var manifest, out blocker))
            return false;

        if (!DadRunSlotManifestRules.RequiresFrozenRoster(command.Plan))
            return true;

        if (command.Participants == null)
            return Fail("Worker command is missing its participant assignment payload.", out blocker);

        if (command.Participants.Count != manifest.Slots.Count)
        {
            return Fail(
                $"Worker command has {command.Participants.Count} assignment row(s); frozen roster requires {manifest.Slots.Count}.",
                out blocker);
        }

        var duplicateSlot = command.Participants
            .GroupBy(static participant => Normalize(participant.AssignedSlotId), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateSlot != null)
            return Fail("Worker command contains a missing or duplicated assigned slot.", out blocker);

        var duplicateSession = command.Participants
            .GroupBy(static participant => Normalize(participant.WorkerSessionId.Value), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateSession != null)
            return Fail("Worker command contains a missing or duplicated worker session.", out blocker);

        foreach (var slot in manifest.Slots)
        {
            var rows = command.Participants
                .Where(participant => Same(participant.AssignedSlotId, slot.SlotId))
                .ToList();
            if (rows.Count != 1)
                return Fail($"{slot.SlotId} does not map to exactly one worker assignment row.", out blocker);

            var row = rows[0];
            if (!Same(row.ManagedAccountKey.Value, slot.AccountKey.Value) ||
                !Same(row.ActiveCharacterKey.Value, slot.CharacterKey.Value) ||
                !Same(row.Character.CharacterKey, slot.CharacterKey.Value) ||
                row.Character.ContentId != slot.ContentId)
            {
                return Fail(
                    $"{slot.SlotId} assignment identity does not match frozen account '{slot.AccountKey}', character '{slot.CharacterKey}', Content ID {slot.ContentId}.",
                    out blocker);
            }
        }

        var localRows = command.Participants.Where(static participant => participant.IsLocalClient).ToList();
        if (localRows.Count != 1)
            return Fail($"Worker command must contain exactly one local assignment row; found {localRows.Count}.", out blocker);

        localAssignment = localRows[0];
        if (!Same(localAssignment.WorkerSessionId.Value, localRuntime.WorkerSessionId.Value) ||
            !Same(localAssignment.ManagedAccountKey.Value, localRuntime.ManagedAccountKey.Value) ||
            !Same(localAssignment.ActiveCharacterKey.Value, localRuntime.ActiveCharacterKey.Value) ||
            !Same(localAssignment.Character.CharacterKey, localRuntime.Character.CharacterKey) ||
            localAssignment.Character.ContentId != localRuntime.Character.ContentId ||
            !Same(localAssignment.AssignedSlotId, localRuntime.AssignedSlotId))
        {
            return Fail(
                $"Local worker assignment is stale or belongs to another worker/account/character/slot (expected session '{localRuntime.WorkerSessionId}', account '{localRuntime.ManagedAccountKey}', character '{localRuntime.ActiveCharacterKey}', slot '{localRuntime.AssignedSlotId}').",
                out blocker);
        }

        if (!localRuntime.IsAvailable || !localRuntime.IsEligibleForRun || localRuntime.State == DadParticipantState.Stale)
            return Fail($"{localAssignment.AssignedSlotId} local runtime assignment is unavailable or stale.", out blocker);

        if (command.Plan.Orchestration.RequirePostArReady && !localRuntime.PostArReady)
            return Fail($"{localAssignment.AssignedSlotId} local runtime assignment is not post-AR ready.", out blocker);

        var localSlotId = localAssignment.AssignedSlotId;
        var frozenSlot = manifest.Slots.Single(slot => Same(slot.SlotId, localSlotId));
        if (frozenSlot.RequiredJobId.HasValue)
        {
            var expectedPreparation = new DadRequestedJobPreparationKey(
                command.RunId,
                localAssignment.WorkerSessionId,
                frozenSlot.SlotId,
                frozenSlot.AccountKey,
                frozenSlot.CharacterKey,
                frozenSlot.ContentId,
                frozenSlot.RequiredJobId);
            if (!DadRequestedJobPreparationProofRules.PermitsReadiness(
                    localAssignment.RequestedJobPreparation,
                    expectedPreparation,
                    localAssignment.Character.CurrentJobId.GetValueOrDefault()))
            {
                return Fail(
                    $"{frozenSlot.SlotId} worker command does not carry exact terminal requested-job preparation proof for job {frozenSlot.RequiredJobId}.",
                    out blocker);
            }

            if (!DadRequestedJobPreparationProofRules.PermitsReadiness(
                    localRuntime.RequestedJobPreparation,
                    expectedPreparation,
                    localRuntime.Character.CurrentJobId.GetValueOrDefault()))
            {
                return Fail(
                    $"{frozenSlot.SlotId} live worker runtime does not have exact terminal requested-job preparation proof for job {frozenSlot.RequiredJobId}.",
                    out blocker);
            }
        }

        var expectedRole = frozenSlot.IsLeader
            ? DadWorkerExecutionRole.QueueLeader
            : DadWorkerExecutionRole.Participant;
        if (command.Role != expectedRole)
        {
            return Fail(
                $"{frozenSlot.SlotId} role is {command.Role}, expected {expectedRole} for frozen character '{frozenSlot.CharacterKey}'.",
                out blocker);
        }

        return true;
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
