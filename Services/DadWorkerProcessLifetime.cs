using System.Diagnostics;
using dad.Models;

namespace dad.Services;

// Evidence is local, process-specific and ephemeral, not an acknowledgement from a replacement worker.
public sealed class DadWorkerProcessLifetime
{
    private readonly string localMachineName;
    private readonly Func<int, DateTime?> readStartTimeUtc;
    private readonly object gate = new();
    private readonly Dictionary<string, (int ProcessId, DateTime StartedAtUtc)> observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> conflicted = new(StringComparer.OrdinalIgnoreCase);

    public DadWorkerProcessLifetime(string? localMachineName = null, Func<int, DateTime?>? readStartTimeUtc = null)
    {
        this.localMachineName = localMachineName ?? Environment.MachineName;
        this.readStartTimeUtc = readStartTimeUtc ?? ReadStartTimeUtc;
    }

    public void Observe(DadParticipantSnapshot participant)
    {
        lock (gate)
            ObserveCore(participant);
    }

    private void ObserveCore(DadParticipantSnapshot participant)
    {
        if (participant.WorkerSessionId.IsEmpty)
            return;
        var key = participant.WorkerSessionId.Value;
        if (observed.TryGetValue(key, out var prior))
        {
            if (prior.ProcessId != participant.ProcessId ||
                !string.Equals(participant.MachineName, localMachineName, StringComparison.OrdinalIgnoreCase))
                conflicted.Add(key);
            return;
        }
        if (participant.ProcessId <= 0 || participant.State == DadParticipantState.Stale ||
            !string.Equals(participant.MachineName, localMachineName, StringComparison.OrdinalIgnoreCase))
            return;
        // Saturation fails closed; do not evict identities still needed by a pending cancellation.
        if (observed.Count >= 4096 || conflicted.Contains(key))
            return;
        try
        {
            if (readStartTimeUtc(participant.ProcessId) is { } started && started != default)
                observed.Add(key, (participant.ProcessId, started));
        }
        catch (Exception)
        {
            // Access denied / unknown process state is never proof of termination.
        }
    }

    public bool HasExited(DadWorkerSessionId workerSessionId)
    {
        lock (gate)
            return HasExitedCore(workerSessionId);
    }

    private bool HasExitedCore(DadWorkerSessionId workerSessionId)
    {
        if (workerSessionId.IsEmpty || conflicted.Contains(workerSessionId.Value) ||
            !observed.TryGetValue(workerSessionId.Value, out var original))
            return false;
        try
        {
            var current = readStartTimeUtc(original.ProcessId);
            return current == null || (current != default(DateTime) && current != original.StartedAtUtc);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DateTime? ReadStartTimeUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null; // Windows reports that this PID no longer exists.
        }
    }
}
