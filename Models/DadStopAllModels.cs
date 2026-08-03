namespace dad.Models;

public enum DadStopAllWorkerState
{
    Expected,
    Acknowledged,
    Rejected,
    Disconnected,
    TimedOut,
}

public sealed class DadStopAllRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public DadWorkerSessionId RequestedByWorkerSessionId { get; set; } = new(string.Empty);
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "Stopped by operator.";
}

public sealed class DadStopAllWorkerResult
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = string.Empty;
    public DadWorkerSessionId WorkerSessionId { get; set; } = new(string.Empty);
    public DadStopAllWorkerState State { get; set; } = DadStopAllWorkerState.Expected;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool LocalCleanupCompleted { get; set; }
    public bool Partial { get; set; }
    public int CancelledSchedulerJobs { get; set; }
    public int CancelledWakeTakeovers { get; set; }
    public int PreservedCommittedTakeovers { get; set; }
    public string Summary { get; set; } = string.Empty;

    public DadStopAllWorkerResult Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            OperationId = OperationId,
            WorkerSessionId = WorkerSessionId,
            State = State,
            UpdatedAtUtc = UpdatedAtUtc,
            LocalCleanupCompleted = LocalCleanupCompleted,
            Partial = Partial,
            CancelledSchedulerJobs = CancelledSchedulerJobs,
            CancelledWakeTakeovers = CancelledWakeTakeovers,
            PreservedCommittedTakeovers = PreservedCommittedTakeovers,
            Summary = Summary,
        };
}

public sealed class DadStopAllStatus
{
    public int SchemaVersion { get; set; } = 1;
    public string OperationId { get; set; } = string.Empty;
    public DadWorkerSessionId RequestedByWorkerSessionId { get; set; } = new(string.Empty);
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public bool RemotePropagationAvailable { get; set; } = true;
    public bool IsFinal { get; set; }
    public bool Partial { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DadStopAllWorkerResult LocalResult { get; set; } = new();
    public List<DadStopAllWorkerResult> Workers { get; set; } = [];

    public DadStopAllStatus Clone()
        => new()
        {
            SchemaVersion = SchemaVersion,
            OperationId = OperationId,
            RequestedByWorkerSessionId = RequestedByWorkerSessionId,
            SubmittedAtUtc = SubmittedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            RemotePropagationAvailable = RemotePropagationAvailable,
            IsFinal = IsFinal,
            Partial = Partial,
            Summary = Summary,
            LocalResult = LocalResult.Clone(),
            Workers = Workers.Select(static worker => worker.Clone()).ToList(),
        };
}

public sealed class DadWakeTakeoverStopAllResult
{
    public int CancelledCount { get; set; }
    public int PreservedCommittedCount { get; set; }
    public bool CleanupPending { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class DadSchedulerStopAllResult
{
    public bool ActiveScheduleCancelled { get; set; }
    public bool ActiveJobCancelled { get; set; }
    public int PendingJobsCancelled { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public static class DadStopAllStatusRules
{
    public const string ActiveRunCoordinatorOnlySummary =
        "Stop-all must be issued from the Coordinator while a run is active.";

    public static void NormalizeLocalResult(DadStopAllWorkerResult result)
    {
        if (result.LocalCleanupCompleted)
        {
            result.State = DadStopAllWorkerState.Acknowledged;
            return;
        }

        // An acknowledgement is only authoritative after every DAD-owned cleanup lease has
        // actually released. Keep the existing wire enum and represent that handshake as Expected.
        if (result.State == DadStopAllWorkerState.Acknowledged)
            result.State = DadStopAllWorkerState.Expected;
    }

    public static bool IsLocalCleanupPending(DadStopAllWorkerResult result)
    {
        NormalizeLocalResult(result);
        return result.State == DadStopAllWorkerState.Expected;
    }

    public static void FinalizeFromWorkers(DadStopAllStatus status, DateTime nowUtc)
    {
        NormalizeLocalResult(status.LocalResult);
        var expected = status.Workers.Count;
        var acknowledged = status.Workers.Count(static worker => worker.State == DadStopAllWorkerState.Acknowledged);
        var rejected = status.Workers.Count(static worker => worker.State == DadStopAllWorkerState.Rejected);
        var disconnected = status.Workers.Count(static worker => worker.State == DadStopAllWorkerState.Disconnected);
        var timedOut = status.Workers.Count(static worker => worker.State == DadStopAllWorkerState.TimedOut);
        var pending = status.Workers.Count(static worker => worker.State == DadStopAllWorkerState.Expected);
        var localPending = IsLocalCleanupPending(status.LocalResult);
        var localFailed = status.LocalResult.State is DadStopAllWorkerState.Rejected or
            DadStopAllWorkerState.Disconnected or DadStopAllWorkerState.TimedOut;
        status.Partial = !status.RemotePropagationAvailable || status.LocalResult.Partial || localFailed ||
                         rejected + disconnected + timedOut > 0 ||
                         status.Workers.Any(static worker => worker.Partial);
        status.IsFinal = !localPending && pending == 0;
        status.CompletedAtUtc = status.IsFinal ? nowUtc : null;
        var local = localPending ? "pending" : status.LocalResult.State.ToString().ToLowerInvariant();
        status.Summary = $"Local cleanup {local}; Stop-all acknowledgements: expected {expected}, acknowledged {acknowledged}, rejected {rejected}, disconnected {disconnected}, timed out {timedOut}, pending {pending}.";
    }
}
