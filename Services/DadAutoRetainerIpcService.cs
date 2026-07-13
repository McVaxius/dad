using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadAutoRetainerState
{
    public bool Available { get; init; }
    // Diagnostic only. A false value never grants DAD mutation ownership.
    public bool IsBusy { get; init; }
    public bool MultiModeEnabled { get; init; }
    public bool SuppressionReadable { get; init; }
    public bool IsSuppressed { get; init; }
    public bool SuppressionOwnedByDad { get; init; }
    public bool CharacterPostprocessOwnedByDad { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class DadAutoRetainerIpcService : IDisposable
{
    private const string PluginName = "Dad";
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool> getMultiModeEnabled;
    private readonly ICallGateSubscriber<bool, object> setMultiModeEnabled;
    private readonly ICallGateSubscriber<bool> getSuppressed;
    private readonly ICallGateSubscriber<bool, object> setSuppressed;
    private readonly ICallGateSubscriber<object> onAdditionalTask;
    private readonly ICallGateSubscriber<string, object> onReadyForPostprocess;
    private readonly ICallGateSubscriber<string, object> requestPostprocess;
    private readonly ICallGateSubscriber<object> finishPostprocess;
    private readonly object gate = new();
    private string armedOperationToken = string.Empty;
    private bool requestSent;
    private bool postprocessOwned;
    private bool finishOnReady;
    private bool suppressionOwned;
    private bool disposed;
    private DateTime lastFinishAttemptUtc = DateTime.MinValue;

    public event Action? CharacterPostprocessReady;

    public DadAutoRetainerIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        isBusy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        getMultiModeEnabled = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled");
        setMultiModeEnabled = pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled");
        getSuppressed = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
        setSuppressed = pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
        onAdditionalTask = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask");
        onReadyForPostprocess = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess");
        requestPostprocess = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess");
        finishPostprocess = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.FinishCharacterPostprocessRequest");
        onAdditionalTask.Subscribe(OnCharacterAdditionalTask);
        onReadyForPostprocess.Subscribe(OnCharacterReadyForPostprocess);
    }

    public bool ArmCharacterPostprocessRequest(string operationToken)
    {
        lock (gate)
        {
            if (disposed || string.IsNullOrWhiteSpace(operationToken))
                return false;
            // A cancelled request that AutoRetainer has accepted but has not yet yielded to Dad
            // remains an owned cleanup boundary. Do not let a later operation replace its token;
            // the deferred callback must finish first and cleanup must observe that completion.
            if (finishOnReady)
                return false;
            if (!string.IsNullOrWhiteSpace(armedOperationToken) &&
                !string.Equals(armedOperationToken, operationToken, StringComparison.OrdinalIgnoreCase))
                return false;
            armedOperationToken = operationToken.Trim();
            return true;
        }
    }

    public DadAutoRetainerState Inspect()
    {
        try
        {
            var suppressed = getSuppressed.InvokeFunc();
            lock (gate)
            {
                if (suppressionOwned && !suppressed)
                    suppressionOwned = false;
                return new DadAutoRetainerState
                {
                    Available = true,
                    IsBusy = isBusy.InvokeFunc(),
                    MultiModeEnabled = getMultiModeEnabled.InvokeFunc(),
                    SuppressionReadable = true,
                    IsSuppressed = suppressed,
                    SuppressionOwnedByDad = suppressionOwned,
                    CharacterPostprocessOwnedByDad = postprocessOwned,
                    Summary = "AutoRetainer handoff IPC available.",
                };
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                return new DadAutoRetainerState
                {
                    // Preserve conservative local ownership even when AR cannot currently be read.
                    // Wake cleanup must not acknowledge until these DAD-owned markers are released.
                    SuppressionOwnedByDad = suppressionOwned,
                    CharacterPostprocessOwnedByDad = postprocessOwned,
                    Summary = $"AutoRetainer handoff IPC unavailable: {ex.Message}",
                };
            }
        }
    }

    public DadSuppressionLeaseSnapshot ReadSuppression()
    {
        try
        {
            var remote = getSuppressed.InvokeFunc();
            lock (gate)
            {
                if (suppressionOwned && !remote)
                    suppressionOwned = false;
                return new DadSuppressionLeaseSnapshot(true, remote, suppressionOwned);
            }
        }
        catch (Exception ex)
        {
            lock (gate)
                return new DadSuppressionLeaseSnapshot(false, false, suppressionOwned, ex.Message);
        }
    }

    public DadWakeTakeoverActionResult TryAcquireSuppression()
    {
        var before = ReadSuppression();
        if (!before.Readable)
            return DadWakeTakeoverActionResult.Rejected($"AutoRetainer suppression is unreadable: {before.Error}");
        if (before.OwnedByDad)
            return DadWakeTakeoverActionResult.Accepted();
        if (before.Suppressed)
            return DadWakeTakeoverActionResult.Rejected("AutoRetainer suppression is externally owned.");

        try
        {
            // Remote state was confirmed clear. Retain conservative local ownership from the
            // write boundary until a later read can prove whether cleanup is required.
            lock (gate)
                suppressionOwned = true;
            setSuppressed.InvokeAction(true);
            var after = ReadSuppression();
            if (!after.Readable || !after.Suppressed)
                return DadWakeTakeoverActionResult.Rejected("DAD could not verify AutoRetainer suppression acquisition.");
            log.Information("[dad][AR] Acquired and verified DAD suppression lease.");
            return DadWakeTakeoverActionResult.Accepted();
        }
        catch (Exception ex)
        {
            var after = ReadSuppression();
            if (after.Readable && !after.Suppressed)
            {
                lock (gate)
                    suppressionOwned = false;
            }
            return DadWakeTakeoverActionResult.Rejected($"AutoRetainer suppression acquisition failed: {ex.Message}");
        }
    }

    public bool ReleaseSuppressionIfOwned(bool force = false)
    {
        lock (gate)
        {
            if (!suppressionOwned)
                return true;
        }

        try
        {
            if (!force)
            {
                var before = ReadSuppression();
                if (!before.Readable)
                    return false;
                if (!before.Suppressed)
                {
                    lock (gate)
                        suppressionOwned = false;
                    return true;
                }
            }

            setSuppressed.InvokeAction(false);
            var after = ReadSuppression();
            if (!after.Readable || after.Suppressed)
                return false;
            lock (gate)
                suppressionOwned = false;
            log.Information("[dad][AR] Released and verified DAD suppression lease.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][AR] Failed to release DAD suppression lease.");
            return false;
        }
    }

    public bool FinishCharacterPostprocess(bool retryAtNextBoundary)
    {
        bool shouldFinish;
        lock (gate)
        {
            shouldFinish = postprocessOwned || requestSent;
            if (!shouldFinish && !retryAtNextBoundary)
            {
                // The request may only be armed locally and not yet sent to AR. Cancellation at
                // that boundary must disarm it so a later character callback cannot resurrect it.
                armedOperationToken = string.Empty;
                finishOnReady = false;
            }
            if (requestSent && !postprocessOwned)
            {
                // AR's finish channel is a global lock release, not a named cancellation. Never
                // invoke it while another plugin may own the current callback. Remember the
                // cancelled Dad request and finish immediately when AR later delivers Dad's turn.
                if (!retryAtNextBoundary)
                {
                    finishOnReady = true;
                    armedOperationToken = string.Empty;
                }
                // The finish has not happened yet. Keep scheduler cancellation cleanup pending
                // until AR delivers Dad's callback, OnCharacterReadyForPostprocess releases it,
                // and a later cleanup poll observes requestSent/postprocessOwned both clear.
                return false;
            }
        }
        if (!shouldFinish)
            return true;

        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (lastFinishAttemptUtc != DateTime.MinValue && now - lastFinishAttemptUtc < TimeSpan.FromSeconds(2))
                return false;
            lastFinishAttemptUtc = now;
        }

        try
        {
            finishPostprocess.InvokeAction();
            lock (gate)
            {
                postprocessOwned = false;
                requestSent = false;
                finishOnReady = false;
                if (!retryAtNextBoundary)
                    armedOperationToken = string.Empty;
                lastFinishAttemptUtc = DateTime.MinValue;
            }
            log.Information("[dad][AR] Finished DAD character postprocess lease (retry={Retry}).", retryAtNextBoundary);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad][AR] Failed to finish DAD character postprocess lease.");
            return false;
        }
    }

    public DadWakeTakeoverActionResult SetMultiModeAndVerify(bool enabled)
    {
        try
        {
            setMultiModeEnabled.InvokeAction(enabled);
            var actual = getMultiModeEnabled.InvokeFunc();
            return actual == enabled
                ? DadWakeTakeoverActionResult.Accepted()
                : DadWakeTakeoverActionResult.Rejected(
                    $"AutoRetainer Multi Mode verification failed; requested {enabled}, observed {actual}.");
        }
        catch (Exception ex)
        {
            return DadWakeTakeoverActionResult.Rejected($"AutoRetainer SetMultiModeEnabled IPC failed: {ex.Message}");
        }
    }

    private void OnCharacterAdditionalTask()
    {
        lock (gate)
        {
            if (disposed || string.IsNullOrWhiteSpace(armedOperationToken) || requestSent || postprocessOwned)
                return;
            // Set first: status and duplicate callbacks must observe the pending ownership request.
            requestSent = true;
        }

        try
        {
            requestPostprocess.InvokeAction(PluginName);
            log.Information("[dad][AR] Requested Dad character postprocess synchronously at OnCharacterAdditionalTask.");
        }
        catch (Exception ex)
        {
            lock (gate)
                requestSent = false;
            log.Warning(ex, "[dad][AR] Character postprocess request failed.");
        }
    }

    private void OnCharacterReadyForPostprocess(string pluginName)
    {
        if (!string.Equals(pluginName, PluginName, StringComparison.OrdinalIgnoreCase))
            return;
        bool finishImmediately;
        lock (gate)
        {
            if (disposed || !requestSent)
                return;
            postprocessOwned = true;
            finishImmediately = finishOnReady;
        }
        if (finishImmediately)
        {
            FinishCharacterPostprocess(retryAtNextBoundary: false);
            return;
        }
        CharacterPostprocessReady?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            lastFinishAttemptUtc = DateTime.MinValue;
        }
        FinishCharacterPostprocess(retryAtNextBoundary: false);
        bool pendingFinish;
        lock (gate)
            pendingFinish = finishOnReady;
        if (pendingFinish)
        {
            try
            {
                // AutoRetainer exposes no named cancellation channel. Disposal is the only path
                // where Dad cannot remain subscribed to finish its later turn, so use the public
                // finish channel best-effort as required by the handoff contract.
                finishPostprocess.InvokeAction();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[dad][AR] Disposal could not finish the pending Dad postprocess request.");
            }
        }
        ReleaseSuppressionIfOwned(force: true);
        onAdditionalTask.Unsubscribe(OnCharacterAdditionalTask);
        onReadyForPostprocess.Unsubscribe(OnCharacterReadyForPostprocess);
    }
}
