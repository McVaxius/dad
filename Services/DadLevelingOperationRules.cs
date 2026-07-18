using dad.Models;

namespace dad.Services;

public static class DadLevelingOperationRules
{
    public static DadLevelingChildDisposition ClassifyChild(
        DadSchedulerPresetPhase schedulerPhase,
        DadRunStatus? runStatus,
        bool dryRun)
    {
        if (schedulerPhase == DadSchedulerPresetPhase.Cancelled || runStatus == DadRunStatus.Cancelled)
            return DadLevelingChildDisposition.Cancel;
        if (schedulerPhase is DadSchedulerPresetPhase.Blocked or DadSchedulerPresetPhase.TimedOut)
            return DadLevelingChildDisposition.Fail;
        if (schedulerPhase == DadSchedulerPresetPhase.Completed)
            return dryRun ? DadLevelingChildDisposition.CompleteDryRun : DadLevelingChildDisposition.Fail;
        if (schedulerPhase != DadSchedulerPresetPhase.StartedPlanner || !runStatus.HasValue ||
            runStatus is DadRunStatus.Idle or DadRunStatus.Queued or DadRunStatus.WaitingForParticipants or DadRunStatus.Running)
        {
            return DadLevelingChildDisposition.Waiting;
        }
        return runStatus == DadRunStatus.Completed
            ? DadLevelingChildDisposition.RefreshAndContinue
            : DadLevelingChildDisposition.Fail;
    }

    public static bool TryValidateExactRosterRefresh(
        DadRosterRefreshCommandDto command,
        DadRosterRefreshResultDto? result,
        out string blocker)
    {
        ArgumentNullException.ThrowIfNull(command);
        blocker = string.Empty;
        if (result == null)
        {
            blocker = "Exact roster refresh reply is pending.";
            return false;
        }
        if (!string.Equals(result.CommandId, command.CommandId, StringComparison.Ordinal) ||
            !DadRosterIdentity.SameAccount(result.AccountKey, command.AccountKey) ||
            !string.Equals(result.CharacterKey.Value, command.CharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            result.ContentId != command.ContentId)
        {
            blocker = "Exact roster refresh reply identity contradicted the frozen request.";
            return false;
        }
        if (!result.Accepted || !result.Success || result.DryRun || !result.RefreshedAtUtc.HasValue ||
            !result.XadbStatus.IsReady || result.XadbStatus.JobLevels == null || result.XadbStatus.JobLevels.Count == 0)
        {
            blocker = $"Exact roster refresh did not prove a complete saved XADB job ledger: {result.Summary}";
            return false;
        }
        return true;
    }
}
