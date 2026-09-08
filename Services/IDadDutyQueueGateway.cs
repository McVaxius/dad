using dad.Models;

namespace dad.Services;

// Native duty UI / sheet / territory boundary. Executors and their admission,
// timeout, completion and restoration decisions remain in production services.
public interface IDadLocalDutyQueueGateway
{
    DadLocalDutyResolvedContent? Resolve(DadDungeonTask? task, out string blocker);
    DadLocalDutyResolvedContent? Resolve(DadPremadeDutyTask? task, out string blocker);
    DadLocalDutyResolvedContent? Resolve(DadDailyMsqTask? task, out string blocker);
    DadLocalDutyResolvedContent? ResolvePremade(DadDungeonTask? task, out string blocker);
    bool CanStart(DadLocalDutyResolvedContent? content, out string blocker);
    DadLocalDutyQueuePulse Pulse(string runId, DadLocalDutyResolvedContent content);
    DadLocalDutyQueuePulse ObserveParticipant(string runId, DadLocalDutyResolvedContent content);
    void ResetParticipantObserver(string runId);
    DadLocalDutyQueuePulse Cancel(string runId, string reason);
    void ResetRun(string runId);
    bool IsInRequestedDuty(DadLocalDutyResolvedContent content);
    bool HasDutyCompleted(DadLocalDutyResolvedContent content, DateTime runStartedAtUtc);
    bool IsQueued();
}

public interface IDadNpcDutyQueueGateway
{
    DadNpcDutyResolvedContent? Resolve(DadDutySupportTask? task, out string blocker);
    DadNpcDutyResolvedContent? Resolve(DadTrustTask? task, out string blocker);
    bool CanSelectTrustPartyForLocalPlayer(out string blocker);
    DadNpcDutyQueuePulse Pulse(string runId, DadNpcDutyResolvedContent content);
    DadNpcDutyQueuePulse Cancel(string runId, DadNpcDutyQueueMode mode, string reason);
    bool IsInRequestedDuty(DadNpcDutyResolvedContent content);
    bool HasDutyCompleted(DadNpcDutyResolvedContent content, DateTime runStartedAtUtc);
    bool IsQueued();
}
