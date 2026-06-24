using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

internal sealed class DadAuthorityStatusPoller : IDisposable
{
    private static readonly TimeSpan SuccessfulPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureBackoffInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(100);

    private readonly object gate = new();
    private readonly DadTransportService transportService;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loopTask;
    private string targetEndpoint = string.Empty;
    private DadWorkerSessionId targetWorkerSessionId = new(string.Empty);
    private DadWorkerRole targetWorkerRole = DadWorkerRole.None;
    private DateTime nextPollUtc = DateTime.MinValue;
    private DadAuthorityStatusPollSnapshot snapshot = DadAuthorityStatusPollSnapshot.Empty;
    private bool targetEnabled;

    public DadAuthorityStatusPoller(DadTransportService transportService, IPluginLog log)
    {
        this.transportService = transportService;
        this.log = log;
        loopTask = Task.Run(() => PollLoopAsync(cancellation.Token));
    }

    public void UpdateTarget(
        string endpoint,
        DadWorkerSessionId workerSessionId,
        DadWorkerRole workerRole,
        bool enabled,
        bool scheduleImmediate = false)
    {
        lock (gate)
        {
            var endpointChanged = !string.Equals(targetEndpoint, endpoint, StringComparison.OrdinalIgnoreCase);
            targetEndpoint = endpoint;
            targetWorkerSessionId = workerSessionId;
            targetWorkerRole = workerRole;
            targetEnabled = enabled && !string.IsNullOrWhiteSpace(endpoint);
            if (endpointChanged)
            {
                snapshot = DadAuthorityStatusPollSnapshot.Empty;
                nextPollUtc = DateTime.MinValue;
                return;
            }

            if (scheduleImmediate)
                nextPollUtc = DateTime.MinValue;
        }
    }

    public void ClearTarget()
    {
        lock (gate)
        {
            targetEndpoint = string.Empty;
            targetWorkerSessionId = new DadWorkerSessionId(string.Empty);
            targetWorkerRole = DadWorkerRole.None;
            targetEnabled = false;
            nextPollUtc = DateTime.MinValue;
            snapshot = DadAuthorityStatusPollSnapshot.Empty;
        }
    }

    public DadAuthorityStatusPollSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return snapshot.Copy();
        }
    }

    public void Dispose()
    {
        try { cancellation.Cancel(); } catch { /* ignore */ }
        try { loopTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { /* best-effort drain */ }
        if (loopTask.IsCompleted)
        {
            try { cancellation.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DadAuthorityPollTarget? target = null;
            lock (gate)
            {
                if (targetEnabled && DateTime.UtcNow >= nextPollUtc)
                {
                    target = new DadAuthorityPollTarget(
                        targetEndpoint,
                        targetWorkerSessionId,
                        targetWorkerRole);
                    nextPollUtc = DateTime.UtcNow + SuccessfulPollInterval;
                }
            }

            if (target == null)
            {
                await DelayIdle(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var pollResult = await transportService.QueryAuthorityStatusPollAsync(target.Endpoint, cancellationToken)
                    .ConfigureAwait(false);
                lock (gate)
                {
                    if (!string.Equals(targetEndpoint, target.Endpoint, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!pollResult.Sent)
                    {
                        nextPollUtc = DateTime.UtcNow + IdleDelay;
                        continue;
                    }

                    if (pollResult.Response != null)
                    {
                        snapshot = new DadAuthorityStatusPollSnapshot(
                            target.Endpoint,
                            target.WorkerSessionId,
                            target.WorkerRole,
                            pollResult.Response.Clone(),
                            DateTime.UtcNow,
                            null,
                            string.Empty);
                        nextPollUtc = DateTime.UtcNow + SuccessfulPollInterval;
                    }
                    else
                    {
                        snapshot = snapshot with
                        {
                            Endpoint = target.Endpoint,
                            WorkerSessionId = target.WorkerSessionId,
                            WorkerRole = target.WorkerRole,
                            LastFailureUtc = DateTime.UtcNow,
                            LastFailureSummary = "Server Dad status query failed.",
                        };
                        nextPollUtc = DateTime.UtcNow + FailureBackoffInterval;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[dad] Authority status poll failed.");
                lock (gate)
                {
                    if (string.Equals(targetEndpoint, target.Endpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        snapshot = snapshot with
                        {
                            Endpoint = target.Endpoint,
                            WorkerSessionId = target.WorkerSessionId,
                            WorkerRole = target.WorkerRole,
                            LastFailureUtc = DateTime.UtcNow,
                            LastFailureSummary = "Server Dad status query failed.",
                        };
                        nextPollUtc = DateTime.UtcNow + FailureBackoffInterval;
                    }
                }
            }
        }
    }

    private static async Task DelayIdle(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(IdleDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}

internal sealed record DadAuthorityStatusPollSnapshot(
    string Endpoint,
    DadWorkerSessionId WorkerSessionId,
    DadWorkerRole WorkerRole,
    DadRunResult? Result,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    string LastFailureSummary)
{
    public static DadAuthorityStatusPollSnapshot Empty { get; } = new(
        string.Empty,
        new DadWorkerSessionId(string.Empty),
        DadWorkerRole.None,
        null,
        null,
        null,
        string.Empty);

    public DadAuthorityStatusPollSnapshot Copy()
        => this with
        {
            Result = Result?.Clone(),
        };
}

internal sealed record DadAuthorityPollTarget(
    string Endpoint,
    DadWorkerSessionId WorkerSessionId,
    DadWorkerRole WorkerRole);
