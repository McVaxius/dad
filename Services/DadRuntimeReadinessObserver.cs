using dad.Models;

namespace dad.Services;

internal sealed class DadRuntimeReadinessObserver(
    DadPresenceService presence, DadCharacterIntelligenceService intelligence,
    DadTransportService transport, DadSchedulerService scheduler,
    DadAutoRetainerIpcService autoRetainer, DadWakeTakeoverService wake,
    Action<string>? invalidate = null)
{
    private readonly DadRuntimeReadinessTracker tracker = new();
    public void ObserveLocal()
    {
        var signature = CaptureLocalRuntimeReadinessSignature();
        if (tracker.WouldChange(signature))
        {
            // A takeover callback can acquire/release suppression after the normal presence pass.
            // Refresh once on a semantic edge so the immediate heartbeat carries final post-AR truth.
            presence.Update(
                intelligence.CurrentPool,
                transport.CurrentTransport.ListenerEndpoint);
            signature = CaptureLocalRuntimeReadinessSignature();
        }

        if (!tracker.Observe(signature, out var revision))
            return;

        invalidate?.Invoke($"local runtime readiness revision {revision}");
        scheduler.WakeForRuntimeReadiness(presence.WorkerSessionId);
        transport.NotifyLocalRuntimeReadinessChanged(revision);
    }

    private DadRuntimeReadinessSignature CaptureLocalRuntimeReadinessSignature()
    {
        var participant = presence.BuildSnapshotCopy();
        var ar = autoRetainer.Inspect();
        return DadRuntimeReadinessSignature.Create(
            participant,
            ar.SuppressionReadable,
            ar.IsSuppressed,
            ar.SuppressionOwnedByDad,
            ar.CharacterPostprocessOwnedByDad,
            wake.GetActiveStatus());
    }

    public void ObserveRemote(DadWorkerSessionId workerSessionId, long revision)
    {
        // Transport applies the heartbeat and refreshes its participant projection before invoking
        // this callback on the framework thread, so the scheduler can consume the edge this tick.
        invalidate?.Invoke($"remote runtime readiness revision {revision} ({workerSessionId.Value})");
        scheduler.WakeForRuntimeReadiness(workerSessionId);
        intelligence.RefreshLocalCharacterPool("remote-runtime-readiness", logRefresh: false);
    }

}
