using dad.Models;

namespace dad.Services;

internal readonly record struct DadLifecycleCleanupDecision(
    bool RunFullCleanup,
    bool RetryTakeoverCleanup,
    bool ReturnRecordedResult);

internal static class DadLifecycleCleanupRules
{
    public static DadLifecycleCleanupDecision Decide(bool hasRecordedResult, bool cleanupPending)
        => !hasRecordedResult
            ? new DadLifecycleCleanupDecision(
                RunFullCleanup: true,
                RetryTakeoverCleanup: true,
                ReturnRecordedResult: false)
            : cleanupPending
                ? new DadLifecycleCleanupDecision(
                    RunFullCleanup: false,
                    RetryTakeoverCleanup: true,
                    ReturnRecordedResult: false)
                : new DadLifecycleCleanupDecision(
                    RunFullCleanup: false,
                    RetryTakeoverCleanup: false,
                    ReturnRecordedResult: true);

    public static bool ShouldFinalizeRosterlessSingleWorker(
        int requiredParticipantCount,
        bool hasSlotManifest,
        int requiredRosterCharacterCount)
        => requiredParticipantCount <= 1 &&
           !hasSlotManifest &&
           requiredRosterCharacterCount == 0;

    public static DadStopAllRequest RebindStopAllFanoutRequester(
        DadStopAllRequest request,
        DadWorkerSessionId authenticatedCoordinator)
        => new()
        {
            SchemaVersion = request.SchemaVersion,
            OperationId = request.OperationId,
            RequestedByWorkerSessionId = authenticatedCoordinator,
            RequestedAtUtc = request.RequestedAtUtc,
            Reason = request.Reason,
        };
}
