namespace dad.Services;

internal enum DadAutoRetainerPostprocessLeaseStage
{
    None = 0,
    Armed = 1,
    RequestSent = 2,
    Owned = 3,
}

internal readonly record struct DadAutoRetainerPostprocessLeaseDecision(
    bool Accepted,
    bool ShouldRequest,
    bool ShouldFinish,
    bool Pending,
    long Generation,
    string SafeCode);

internal sealed class DadAutoRetainerPostprocessLease
{
    internal static readonly TimeSpan PendingRequestTimeout = TimeSpan.FromMinutes(2);

    private string operationToken = string.Empty;
    private DateTime requestSentAtUtc = DateTime.MinValue;
    private bool finishOnReady;

    internal long Generation { get; private set; }
    internal DadAutoRetainerPostprocessLeaseStage Stage { get; private set; }
    internal bool IsOwned => Stage == DadAutoRetainerPostprocessLeaseStage.Owned;
    internal bool IsPending => Stage == DadAutoRetainerPostprocessLeaseStage.RequestSent;
    internal string OperationToken => operationToken;

    internal DadAutoRetainerPostprocessLeaseDecision Arm(string token, DateTime nowUtc)
    {
        ExpirePending(nowUtc);
        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            return Decision(false, safeCode: "dad-ar-postprocess-token-invalid");
        if (Stage is DadAutoRetainerPostprocessLeaseStage.RequestSent or DadAutoRetainerPostprocessLeaseStage.Owned)
            return Decision(false, pending: Stage == DadAutoRetainerPostprocessLeaseStage.RequestSent,
                safeCode: "dad-ar-postprocess-generation-active");
        if (Stage == DadAutoRetainerPostprocessLeaseStage.Armed &&
            !string.Equals(operationToken, token, StringComparison.OrdinalIgnoreCase))
            return Decision(false, safeCode: "dad-ar-postprocess-other-operation-armed");

        if (Stage == DadAutoRetainerPostprocessLeaseStage.None)
            Generation++;
        operationToken = token;
        finishOnReady = false;
        Stage = DadAutoRetainerPostprocessLeaseStage.Armed;
        return Decision(true, safeCode: "dad-ar-postprocess-armed");
    }

    internal DadAutoRetainerPostprocessLeaseDecision BeginRequest(DateTime nowUtc)
    {
        ExpirePending(nowUtc);
        if (Stage != DadAutoRetainerPostprocessLeaseStage.Armed || string.IsNullOrWhiteSpace(operationToken))
            return Decision(false, safeCode: "dad-ar-postprocess-not-armed");
        Stage = DadAutoRetainerPostprocessLeaseStage.RequestSent;
        requestSentAtUtc = NormalizeUtc(nowUtc);
        return Decision(true, shouldRequest: true, pending: true, safeCode: "dad-ar-postprocess-request-sent");
    }

    internal void MarkRequestFault(long generation)
    {
        if (generation != Generation || Stage != DadAutoRetainerPostprocessLeaseStage.RequestSent)
            return;
        // The IPC throw does not prove whether AutoRetainer registered the request. Retain the
        // generation until timeout; a late Dad callback is immediately released.
        finishOnReady = true;
    }

    internal DadAutoRetainerPostprocessLeaseDecision MarkReady(DateTime nowUtc)
    {
        ExpirePending(nowUtc);
        if (Stage == DadAutoRetainerPostprocessLeaseStage.RequestSent)
        {
            Stage = DadAutoRetainerPostprocessLeaseStage.Owned;
            requestSentAtUtc = DateTime.MinValue;
            return Decision(true, shouldFinish: finishOnReady, safeCode: "dad-ar-postprocess-owned");
        }

        // AutoRetainer named Dad in the callback, so the global lease is Dad-owned even if the
        // originating request already timed out locally. Release this stale generation at once.
        Generation++;
        operationToken = string.Empty;
        finishOnReady = true;
        Stage = DadAutoRetainerPostprocessLeaseStage.Owned;
        return Decision(true, shouldFinish: true, safeCode: "dad-ar-postprocess-stale-callback-owned");
    }

    internal DadAutoRetainerPostprocessLeaseDecision RequestFinish(bool retryAtNextBoundary, DateTime nowUtc)
    {
        ExpirePending(nowUtc);
        if (Stage == DadAutoRetainerPostprocessLeaseStage.Owned)
            return Decision(true, shouldFinish: true, safeCode: "dad-ar-postprocess-finish-owned");
        if (Stage == DadAutoRetainerPostprocessLeaseStage.RequestSent)
        {
            if (!retryAtNextBoundary)
            {
                finishOnReady = true;
                operationToken = string.Empty;
            }
            return Decision(false, pending: true, safeCode: "dad-ar-postprocess-awaiting-owned-callback");
        }
        if (Stage == DadAutoRetainerPostprocessLeaseStage.Armed && !retryAtNextBoundary)
            Reset();
        return Decision(true, safeCode: "dad-ar-postprocess-no-owned-lease");
    }

    internal void FinishSucceeded(long generation, bool retryAtNextBoundary)
    {
        if (generation != Generation || Stage != DadAutoRetainerPostprocessLeaseStage.Owned)
            return;
        requestSentAtUtc = DateTime.MinValue;
        finishOnReady = false;
        if (retryAtNextBoundary && !string.IsNullOrWhiteSpace(operationToken))
            Stage = DadAutoRetainerPostprocessLeaseStage.Armed;
        else
            Reset();
    }

    internal bool ExpirePending(DateTime nowUtc)
    {
        nowUtc = NormalizeUtc(nowUtc);
        if (Stage != DadAutoRetainerPostprocessLeaseStage.RequestSent ||
            requestSentAtUtc == DateTime.MinValue ||
            nowUtc - requestSentAtUtc < PendingRequestTimeout)
            return false;

        requestSentAtUtc = DateTime.MinValue;
        finishOnReady = false;
        Stage = string.IsNullOrWhiteSpace(operationToken)
            ? DadAutoRetainerPostprocessLeaseStage.None
            : DadAutoRetainerPostprocessLeaseStage.Armed;
        return true;
    }

    internal DadAutoRetainerPostprocessLeaseDecision DisposeDecision()
    {
        if (Stage == DadAutoRetainerPostprocessLeaseStage.Owned)
            return Decision(true, shouldFinish: true, safeCode: "dad-ar-postprocess-dispose-owned");
        Reset();
        return Decision(true, safeCode: "dad-ar-postprocess-dispose-no-owned-lease");
    }

    private void Reset()
    {
        operationToken = string.Empty;
        requestSentAtUtc = DateTime.MinValue;
        finishOnReady = false;
        Stage = DadAutoRetainerPostprocessLeaseStage.None;
    }

    private DadAutoRetainerPostprocessLeaseDecision Decision(
        bool accepted,
        bool shouldRequest = false,
        bool shouldFinish = false,
        bool pending = false,
        string safeCode = "")
        => new(accepted, shouldRequest, shouldFinish, pending, Generation, safeCode);

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
