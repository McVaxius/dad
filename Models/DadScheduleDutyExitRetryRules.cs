namespace dad.Models;

public enum DadDutyExitRetryAction { Wait, Retry, Block }

public readonly record struct DadDutyExitRetryDecision(DadDutyExitRetryAction Action, string Summary);

public static class DadScheduleDutyExitRetryRules
{
    public const int MaximumRetries = 1;
    public static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(5);

    public static bool CanReserve(DadScheduleRunState state, DadRunResult run)
        => RequiresPartyTeardownBeforeRetry(state, run) &&
           run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted;

    public static bool RequiresPartyTeardownBeforeRetry(DadScheduleRunState state, DadRunResult run)
        => state.IsActive && state.Phase == DadScheduleRunPhase.WaitingForDadRun && !state.DryRun &&
           state.DutyExitRetryCount == 0 && state.DutyExitRetrySource == null &&
           Same(state.ActivePlannerRequestId, run.RequestId) &&
           run.Status is DadRunStatus.Failed or DadRunStatus.PartialFailure &&
           run.ScheduleFailureKind == DadScheduleFailureKind.EntryTerminalFailure &&
           run.CurrentExecutorStatus.DutyExitedWithoutCompletion &&
           run.ModuleId is DadModuleId.PremadeDuty or DadModuleId.DailyMsq or DadModuleId.Duty &&
           run.CompletedAtUtc.HasValue && run.StopProgress.CompletedRuns == 0 &&
           run.Request is { } request && request.GetConfiguredTaskCount() == 1 &&
           request.Orchestration.AuthorityMode == DadAuthorityMode.ServerDad &&
           HasExactRoster(run);

    public static DadScheduleRunState Reserve(DadScheduleRunState state, DadRunResult run, DateTime now)
    {
        if (!CanReserve(state, run))
            throw new InvalidOperationException("The failed entry is not eligible for an automatic duty-exit retry.");
        var result = state.Clone();
        result.DutyExitRetryCount = MaximumRetries;
        result.DutyExitRetrySource = run.Clone();
        result.DutyExitRetryPendingSinceUtc = now;
        result.UpdatedAtUtc = now;
        result.Summary = "Duty exited without completion; automatic retry 1/1 reserved. Waiting for exact cleanup and safe live clients before rechecking skip conditions.";
        return result;
    }

    public static DadDutyExitRetryDecision EvaluateReadiness(
        DadScheduleRunState state, IReadOnlyList<DadParticipantSnapshot> live,
        bool cleanupPending, string admissionBlocker, DateTime now, TimeSpan heartbeatBudget)
    {
        var source = state.DutyExitRetrySource;
        if (state.DutyExitRetryCount != MaximumRetries || source?.CompletedAtUtc is not { } completed ||
            !source.CurrentExecutorStatus.DutyExitPartyTeardownCompleted || !HasExactRoster(source))
            return Block("Automatic retry lost its exact failed-run provenance.");
        var since = state.DutyExitRetryPendingSinceUtc ?? completed;
        if (now - since >= ReadinessBudget)
            return Block("Automatic retry 1/1 could not obtain exact cleanup and safe live clients within five minutes; no retry was started.");
        if (cleanupPending || !string.IsNullOrWhiteSpace(admissionBlocker))
            return Wait("Automatic retry 1/1 is waiting for acknowledged cleanup and idle DAD admission.");

        foreach (var expected in source.Participants)
        {
            var matches = live.Where(row => Same(row.WorkerSessionId.Value, expected.WorkerSessionId.Value)).ToList();
            if (matches.Count > 1)
                return Block("Automatic retry found an ambiguous worker identity.");
            if (matches.Count == 0)
                return Wait($"Automatic retry is waiting for the original worker for {expected.AssignedSlotId} to reconnect.");
            var row = matches[0];
            if (!row.IsAvailable || !row.WorldReadyStable || !row.PostArReady || row.AutoRetainerBusy ||
                row.ExternalAutomationHeld || row.LastHeartbeatUtc <= completed ||
                row.LastHeartbeatUtc > now || now - row.LastHeartbeatUtc > heartbeatBudget)
                return Wait($"Automatic retry is waiting for fresh, world-ready, outside-duty proof for {expected.AssignedSlotId}.");
            if (!DadRosterIdentity.SameAccount(row.ManagedAccountKey, expected.ManagedAccountKey) ||
                !Same(row.ActiveCharacterKey.Value, expected.ActiveCharacterKey.Value) ||
                !Same(row.Character.CharacterKey, expected.ActiveCharacterKey.Value) ||
                row.Character.ContentId != expected.Character.ContentId ||
                (!string.IsNullOrWhiteSpace(row.RunId) && !Same(row.RunId, source.RequestId)))
                return Block($"Automatic retry refused changed character/account/run ownership for {expected.AssignedSlotId}.");
        }
        return new(DadDutyExitRetryAction.Retry, "Automatic retry 1/1 is ready for normal level/daily preflight and exact party assembly.");
    }

    public static bool SameSelection(DadRunRequest? expected, DadRunRequest? candidate)
    {
        if (expected == null || candidate == null || expected.GetConfiguredTaskCount() != 1 || candidate.GetConfiguredTaskCount() != 1)
            return false;
        var a = expected.Orchestration;
        var b = candidate.Orchestration;
        if (a.ModuleTarget != b.ModuleTarget || a.AuthorityMode != b.AuthorityMode ||
            a.TransportMode != b.TransportMode || a.QueueAuthority != b.QueueAuthority ||
            a.InviteAuthority != b.InviteAuthority || a.LocalOnlyOverride != b.LocalOnlyOverride ||
            !Same(a.PreferredLeaderCharacterKey.Value, b.PreferredLeaderCharacterKey.Value) ||
            !Same(a.PreferredInviterCharacterKey.Value, b.PreferredInviterCharacterKey.Value) ||
            a.RequiredRosterCharacters.Count == 0 || a.RequiredRosterCharacters.Count != b.RequiredRosterCharacters.Count)
            return false;
        for (var i = 0; i < a.RequiredRosterCharacters.Count; i++)
        {
            var x = a.RequiredRosterCharacters[i];
            var y = b.RequiredRosterCharacters[i];
            if (!DadRosterIdentity.SameAccount(x.AccountKey, y.AccountKey) || !Same(x.CharacterKey.Value, y.CharacterKey.Value) ||
                x.ContentId != y.ContentId || x.RequiredJobId != y.RequiredJobId || !string.IsNullOrEmpty(y.SharedIdentityToken))
                return false;
        }
        if (expected.PremadeDuty is { } duty)
            return candidate.PremadeDuty is { } other && duty.ContentFinderConditionId == other.ContentFinderConditionId &&
                   duty.Unsynced == other.Unsynced && duty.ExpectedPartySize == other.ExpectedPartySize;
        if (expected.DailyMsq is { } daily)
            return candidate.DailyMsq is { } other && daily.QueueTarget.Kind == other.QueueTarget.Kind &&
                   daily.QueueTarget.RouletteId == other.QueueTarget.RouletteId &&
                   daily.QueueTarget.ContentFinderConditionId == other.QueueTarget.ContentFinderConditionId;
        return expected.Dungeon is { QueueViaLanParty: true } dungeon && candidate.Dungeon is { QueueViaLanParty: true } otherDungeon &&
               dungeon.ContentFinderConditionId == otherDungeon.ContentFinderConditionId && dungeon.Unsynced == otherDungeon.Unsynced;
    }

    private static bool HasExactRoster(DadRunResult run)
    {
        var rows = run.Request?.Orchestration.RequiredRosterCharacters;
        if (rows == null || rows.Count == 0 || rows.Count != run.Participants.Count ||
            run.Participants.Select(row => row.WorkerSessionId.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count ||
            rows.Select(row => row.CharacterKey.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            return false;
        return rows.All(expected => expected.ContentId != 0 && !expected.AccountKey.IsEmpty && !expected.CharacterKey.IsEmpty &&
            string.IsNullOrEmpty(expected.SharedIdentityToken) && run.Participants.Count(row =>
                !row.WorkerSessionId.IsEmpty && string.IsNullOrEmpty(row.RegisteredIslandId) &&
                DadRosterIdentity.SameAccount(row.ManagedAccountKey, expected.AccountKey) &&
                Same(row.ActiveCharacterKey.Value, expected.CharacterKey.Value) && row.Character.ContentId == expected.ContentId) == 1);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static DadDutyExitRetryDecision Wait(string summary) => new(DadDutyExitRetryAction.Wait, summary);
    private static DadDutyExitRetryDecision Block(string summary) => new(DadDutyExitRetryAction.Block, summary);
}
