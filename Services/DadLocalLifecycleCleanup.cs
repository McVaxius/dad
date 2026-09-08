using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

// The same idempotent cleanup and acknowledgement path is used for LAN delivery,
// local Stop All, plugin shutdown, and headless shutdown.
internal sealed class DadLocalLifecycleCleanup(
    Configuration Configuration, DadSchedulerService SchedulerService,
    DadCoordinatorService RunCoordinatorService, DadWakeTakeoverService WakeTakeoverService,
    DadClaimService ClaimService, DadWorkerExecutionService WorkerExecutionService,
    DadQueueExecutionService QueueExecutionService, DadPresenceService PresenceService,
    IPluginLog Log, Action<string> CancelStandaloneCrewDisband,
    DadAutoPartyService? AutoPartyService, DadAlliancePartyFinderService? AlliancePartyFinderService,
    Action<string>? CancelDutyBridge = null)
{
    private readonly Dictionary<string, DadStopAllWorkerResult> localStopAllResults = new(StringComparer.OrdinalIgnoreCase);

    public DadStopAllWorkerResult RunLocalLifecycleCleanup(DadStopAllRequest request)
    {
        var hasRecordedResult = localStopAllResults.TryGetValue(request.OperationId, out var recorded);
        var decision = DadLifecycleCleanupRules.Decide(
            hasRecordedResult,
            hasRecordedResult && DadStopAllStatusRules.IsLocalCleanupPending(recorded!));
        if (decision.ReturnRecordedResult)
            return recorded!.Clone();

        try
        {
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Stopped by DAD Stop-all." : request.Reason;
            DadStopAllWorkerResult result;
            DadWakeTakeoverStopAllResult wake;
            if (decision.RunFullCleanup)
            {
                var suppression = TimeSpan.FromSeconds(Math.Max(2, Configuration.CancelAckTimeoutSeconds));
                CancelStandaloneCrewDisband(reason);
                var scheduler = SchedulerService.StopAll(reason, suppression);
                AutoPartyService?.StopAll(reason);
                AlliancePartyFinderService?.Stop(reason);
                RunCoordinatorService.CancelAllLocal(reason);
                wake = WakeTakeoverService.StopAll(reason);
                ClaimService.ReleaseAllClaims();
                WorkerExecutionService.CancelAll(reason);
                QueueExecutionService.CancelAll(reason);
                CancelDutyBridge?.Invoke(reason);
                PresenceService.ResetToIdle();
                result = new DadStopAllWorkerResult
                {
                    OperationId = request.OperationId,
                    WorkerSessionId = PresenceService.WorkerSessionId,
                    CancelledSchedulerJobs = scheduler.PendingJobsCancelled + (scheduler.ActiveJobCancelled ? 1 : 0),
                    Summary = scheduler.Summary,
                };
            }
            else
            {
                // Repeated delivery of the same operation is the cleanup acknowledgement poll. All
                // broad stop mutations already ran; only retry DAD-owned takeover lease release.
                result = recorded!.Clone();
                wake = WakeTakeoverService.StopAll(reason);
            }

            result.UpdatedAtUtc = DadClock.UtcNow;
            result.LocalCleanupCompleted = !wake.CleanupPending;
            result.State = wake.CleanupPending
                ? DadStopAllWorkerState.Expected
                : DadStopAllWorkerState.Acknowledged;
            result.CancelledWakeTakeovers = Math.Max(result.CancelledWakeTakeovers, wake.CancelledCount);
            result.PreservedCommittedTakeovers = Math.Max(
                result.PreservedCommittedTakeovers,
                wake.PreservedCommittedCount);
            result.Partial = result.PreservedCommittedTakeovers > 0;
            var summary = hasRecordedResult
                ? result.Summary
                : $"{result.Summary} {wake.Summary}";
            result.Summary = WithStopAllCleanupState(summary, wake.CleanupPending);
            DadStopAllStatusRules.NormalizeLocalResult(result);
            localStopAllResults[request.OperationId] = result.Clone();
            while (localStopAllResults.Count > 32)
                localStopAllResults.Remove(localStopAllResults.Keys.First());
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[dad] Stop-all {OperationId} local cleanup failed.", request.OperationId);
            var result = hasRecordedResult ? recorded!.Clone() : new DadStopAllWorkerResult();
            result.OperationId = request.OperationId;
            result.WorkerSessionId = PresenceService.WorkerSessionId;
            result.State = DadStopAllWorkerState.Rejected;
            result.UpdatedAtUtc = DadClock.UtcNow;
            result.LocalCleanupCompleted = false;
            result.Partial = true;
            result.Summary = $"Local Stop-all cleanup failed: {ex.Message}";
            localStopAllResults[request.OperationId] = result.Clone();
            return result;
        }
    }

    private static string WithStopAllCleanupState(string summary, bool cleanupPending)
    {
        const string pendingSuffix = " DAD-owned takeover cleanup is pending; acknowledgement will follow after all temporary leases release.";
        summary = (summary ?? string.Empty).Trim();
        if (summary.EndsWith(pendingSuffix.TrimStart(), StringComparison.Ordinal))
            summary = summary[..^pendingSuffix.TrimStart().Length].TrimEnd();
        return cleanupPending ? $"{summary}{pendingSuffix}".Trim() : summary;
    }

}
