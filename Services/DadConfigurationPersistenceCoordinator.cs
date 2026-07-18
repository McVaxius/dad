using System.ComponentModel;

namespace dad.Services;

internal sealed class DadConfigurationPersistenceOptions
{
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumAutomaticRetries { get; init; } = 3;
}

internal sealed record DadConfigurationPersistenceState(
    bool IsDirty,
    bool HasFault,
    bool IsLatched,
    bool ManualRetryQueued,
    int ConsecutiveFailures,
    DateTime? NextRetryAtUtc,
    DateTime? LastSuccessfulSaveUtc,
    string FailureSummary);

internal sealed record DadConfigurationPersistenceFailure(
    Exception Exception,
    int ConsecutiveFailureCount,
    bool IsInvalidHandle,
    bool WillRetry,
    DateTime? NextRetryAtUtc);

/// <summary>
/// Coalesces configuration writes onto the framework thread and contains every storage exception.
/// MarkDirty is safe to call from any thread; Update and ForceFlush own the actual write boundary.
/// </summary>
internal sealed class DadConfigurationPersistenceCoordinator
{
    internal const int InvalidHandleNativeError = 6;

    private readonly object syncRoot = new();
    private readonly Action persist;
    private readonly Func<DateTime> utcNow;
    private readonly DadConfigurationPersistenceOptions options;
    private readonly Action<DadConfigurationPersistenceFailure>? onFailure;
    private bool dirty;
    private DateTime firstDirtyAtUtc = DateTime.MinValue;
    private DateTime lastDirtyAtUtc = DateTime.MinValue;
    private bool writeInProgress;
    private bool manualRetryQueued;
    private bool latched;
    private int automaticRetriesScheduled;
    private int consecutiveFailures;
    private DateTime? nextRetryAtUtc;
    private DateTime? lastSuccessfulSaveUtc;
    private Exception? lastFailure;
    private bool lastFailureWasInvalidHandle;

    public DadConfigurationPersistenceCoordinator(
        Action persist,
        Func<DateTime>? utcNow = null,
        DadConfigurationPersistenceOptions? options = null,
        Action<DadConfigurationPersistenceFailure>? onFailure = null)
    {
        this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.options = options ?? new DadConfigurationPersistenceOptions();
        this.onFailure = onFailure;

        if (this.options.QuietPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Quiet period cannot be negative.");
        if (this.options.MaximumDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum delay cannot be negative.");
        if (this.options.RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay cannot be negative.");
        if (this.options.MaximumAutomaticRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum retry count cannot be negative.");
    }

    public void MarkDirty()
    {
        var now = utcNow();
        lock (syncRoot)
        {
            if (!dirty)
                firstDirtyAtUtc = now;
            dirty = true;
            lastDirtyAtUtc = now;
        }
    }

    public void QueueManualRetry()
    {
        var now = utcNow();
        lock (syncRoot)
        {
            if (!dirty)
            {
                dirty = true;
                firstDirtyAtUtc = now;
                lastDirtyAtUtc = now;
            }

            latched = false;
            automaticRetriesScheduled = 0;
            nextRetryAtUtc = null;
            manualRetryQueued = true;
        }
    }

    public bool Update()
        => TryFlush(force: false);

    public bool ForceFlush()
        => TryFlush(force: true);

    public DadConfigurationPersistenceState GetState()
    {
        lock (syncRoot)
        {
            return new DadConfigurationPersistenceState(
                dirty,
                lastFailure != null,
                latched,
                manualRetryQueued,
                consecutiveFailures,
                nextRetryAtUtc,
                lastSuccessfulSaveUtc,
                BuildFailureSummary(lastFailure, lastFailureWasInvalidHandle));
        }
    }

    private bool TryFlush(bool force)
    {
        DirtyBatch batch;
        var now = utcNow();
        lock (syncRoot)
        {
            if (!dirty || writeInProgress || (!force && latched))
                return false;

            if (!force && !manualRetryQueued)
            {
                if (nextRetryAtUtc.HasValue)
                {
                    if (now < nextRetryAtUtc.Value)
                        return false;
                }
                else
                {
                    var quietElapsed = now - lastDirtyAtUtc >= options.QuietPeriod;
                    var maximumElapsed = now - firstDirtyAtUtc >= options.MaximumDelay;
                    if (!quietElapsed && !maximumElapsed)
                        return false;
                }
            }

            batch = new DirtyBatch(firstDirtyAtUtc, lastDirtyAtUtc);
            dirty = false;
            firstDirtyAtUtc = DateTime.MinValue;
            lastDirtyAtUtc = DateTime.MinValue;
            writeInProgress = true;
            manualRetryQueued = false;
        }

        try
        {
            persist();
        }
        catch (Exception ex)
        {
            HandleFailure(batch, ex);
            return true;
        }

        lock (syncRoot)
        {
            writeInProgress = false;
            latched = false;
            automaticRetriesScheduled = 0;
            consecutiveFailures = 0;
            nextRetryAtUtc = null;
            lastFailure = null;
            lastFailureWasInvalidHandle = false;
            lastSuccessfulSaveUtc = utcNow();
        }

        return true;
    }

    private void HandleFailure(DirtyBatch batch, Exception exception)
    {
        DadConfigurationPersistenceFailure failure;
        lock (syncRoot)
        {
            var failedAtUtc = utcNow();
            writeInProgress = false;
            if (!dirty)
            {
                dirty = true;
                firstDirtyAtUtc = batch.FirstDirtyAtUtc;
                lastDirtyAtUtc = batch.LastDirtyAtUtc;
            }
            else
            {
                firstDirtyAtUtc = Min(firstDirtyAtUtc, batch.FirstDirtyAtUtc);
                lastDirtyAtUtc = Max(lastDirtyAtUtc, batch.LastDirtyAtUtc);
            }

            consecutiveFailures++;
            lastFailure = exception;
            lastFailureWasInvalidHandle = IsInvalidHandle(exception);
            var willRetry = false;
            if (lastFailureWasInvalidHandle)
            {
                latched = true;
                nextRetryAtUtc = null;
            }
            else if (automaticRetriesScheduled < options.MaximumAutomaticRetries)
            {
                automaticRetriesScheduled++;
                nextRetryAtUtc = failedAtUtc + options.RetryDelay;
                willRetry = true;
            }
            else
            {
                latched = true;
                nextRetryAtUtc = null;
            }

            failure = new DadConfigurationPersistenceFailure(
                exception,
                consecutiveFailures,
                lastFailureWasInvalidHandle,
                willRetry,
                nextRetryAtUtc);
        }

        try
        {
            onFailure?.Invoke(failure);
        }
        catch
        {
            // Logging/reporting is advisory. Persistence failures must never escape this boundary.
        }
    }

    private static bool IsInvalidHandle(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is Win32Exception win32 && win32.NativeErrorCode == InvalidHandleNativeError)
                return true;
            if ((current.HResult & 0xFFFF) == InvalidHandleNativeError)
                return true;
        }

        return false;
    }

    private static string BuildFailureSummary(Exception? exception, bool invalidHandle)
    {
        if (exception == null)
            return string.Empty;
        return invalidHandle
            ? $"Invalid handle (native error {InvalidHandleNativeError})."
            : $"{exception.GetType().Name} while writing configuration.";
    }

    private static DateTime Min(DateTime left, DateTime right)
        => left <= right ? left : right;

    private static DateTime Max(DateTime left, DateTime right)
        => left >= right ? left : right;

    private readonly record struct DirtyBatch(DateTime FirstDirtyAtUtc, DateTime LastDirtyAtUtc);
}
