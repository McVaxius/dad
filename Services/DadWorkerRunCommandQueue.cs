using dad.Models;

namespace dad.Services;

internal enum DadWorkerRunQueueAdmission
{
    Enqueued,
    Duplicate,
    Rejected,
}

internal sealed class DadWorkerRunCommandQueue
{
    private readonly Queue<DadWorkerExecutionCommand> commands = new();
    private readonly HashSet<string> commandIds = new(StringComparer.Ordinal);

    public string OwnedRunId { get; private set; } = string.Empty;
    public bool IsEmpty => commands.Count == 0;

    public DadWorkerRunQueueAdmission Enqueue(
        DadWorkerExecutionCommand command,
        out string blocker)
    {
        blocker = string.Empty;
        if (commandIds.Contains(command.CommandId))
            return DadWorkerRunQueueAdmission.Duplicate;

        if (!string.IsNullOrWhiteSpace(OwnedRunId) &&
            !string.Equals(OwnedRunId, command.RunId, StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"Worker already owns run {OwnedRunId}.";
            return DadWorkerRunQueueAdmission.Rejected;
        }

        OwnedRunId = command.RunId;
        commands.Enqueue(command);
        commandIds.Add(command.CommandId);
        return DadWorkerRunQueueAdmission.Enqueued;
    }

    public bool TryDequeue(out DadWorkerExecutionCommand command)
    {
        if (!commands.TryDequeue(out command!))
            return false;

        commandIds.Remove(command.CommandId);
        return true;
    }

    public IReadOnlyList<DadWorkerExecutionCommand> DrainRun(string runId)
    {
        var removed = new List<DadWorkerExecutionCommand>();
        var retained = new Queue<DadWorkerExecutionCommand>();
        while (commands.TryDequeue(out var command))
        {
            if (string.Equals(command.RunId, runId, StringComparison.OrdinalIgnoreCase))
            {
                commandIds.Remove(command.CommandId);
                removed.Add(command);
            }
            else
            {
                retained.Enqueue(command);
            }
        }

        while (retained.TryDequeue(out var command))
            commands.Enqueue(command);

        return removed;
    }

    public IReadOnlyList<DadWorkerExecutionCommand> DrainAll()
    {
        var removed = new List<DadWorkerExecutionCommand>();
        while (commands.TryDequeue(out var command))
            removed.Add(command);
        commandIds.Clear();
        OwnedRunId = string.Empty;
        return removed;
    }

    public void ReleaseOwnershipIfIdle(bool hasActiveCommand)
    {
        if (!hasActiveCommand && commands.Count == 0)
            OwnedRunId = string.Empty;
    }
}

internal static class DadWorkerRecordedCommandValidationRules
{
    public static bool TryValidate(
        DadWorkerExecutionCommand command,
        out string blocker)
    {
        var recordedRuntime = command.Participants?
            .SingleOrDefault(static participant => participant.IsLocalClient)
            ?.Clone() ?? new DadParticipantSnapshot();
        recordedRuntime.IsAvailable = true;
        recordedRuntime.IsEligibleForRun = true;
        recordedRuntime.PostArReady = true;
        recordedRuntime.State = DadParticipantState.Ready;
        return DadWorkerCommandValidationRules.TryValidate(
            command,
            recordedRuntime,
            out _,
            out blocker);
    }
}
