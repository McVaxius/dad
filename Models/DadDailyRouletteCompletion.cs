namespace dad.Models;

public enum DadDailyRouletteProof
{
    Unknown,
    RewardReceived,
    CompletedDuty,
}

// Durable evidence, not an attempt log. Missing legacy evidence never implies completion.
public sealed class DadDailyRouletteCompletion
{
    public DadAccountKey AccountKey { get; set; } = new(string.Empty);
    public DadCharacterKey CharacterKey { get; set; } = new(string.Empty);
    public ulong ContentId { get; set; }
    public uint RouletteId { get; set; }
    public string RouletteKey { get; set; } = string.Empty;
    public DateTime ResetBoundaryUtc { get; set; }
    public DateTime ConfirmedAtUtc { get; set; }
    public string EvidenceId { get; set; } = string.Empty;
    public DadDailyRouletteProof Proof { get; set; }
    public int RewardTextProofVersion { get; set; }
}

public static class DadDailyRouletteCompletionRules
{
    public const int MaximumRecords = 4096;

    public static bool HasCurrentProof(IEnumerable<DadDailyRouletteCompletion>? records,
        DadAccountKey account, DadCharacterKey character, ulong contentId, DadQueueTarget target, DateTime now)
        => ValidIdentity(account, character, contentId, target) &&
           (records ?? []).Any(row => IsCurrent(row, now) &&
               DadRosterIdentity.SameAccount(row.AccountKey, account) &&
               string.Equals(row.CharacterKey.Value, character.Value, StringComparison.OrdinalIgnoreCase) &&
               row.ContentId == contentId && row.RouletteId == target.RouletteId &&
               string.Equals(row.RouletteKey, target.Key, StringComparison.OrdinalIgnoreCase));

    public static bool RecordReward(List<DadDailyRouletteCompletion> records,
        DadRouletteRewardProbeRequestDto request, DadRouletteRewardProbeResultDto response, DateTime now)
    {
        if (request.Operation != DadRouletteRewardProbeOperation.Inspect ||
            !DadRouletteRewardProbeIdentityRules.HasCurrentReceivedTextProof(response) ||
            !DadRouletteRewardProbeIdentityRules.TryValidateResponse(request, response, now, out _) ||
            !SamePeriod(request.RequestedAtUtc, response.RespondedAtUtc, now))
            return false;
        return Insert(records, request.AccountKey, request.CharacterKey, request.CharacterContentId,
            new DadQueueTarget { Kind = DadQueueTargetKind.Roulette, RouletteId = request.RouletteId, Key = request.RouletteKey },
            response.RespondedAtUtc, request.OperationId, DadDailyRouletteProof.RewardReceived, now);
    }

    public static bool RecordCompletedWorker(List<DadDailyRouletteCompletion> records,
        DadWorkerExecutionCommand command, DadWorkerExecutionStatus worker, DadParticipantSnapshot participant, DateTime now)
    {
        var target = command.Plan.Request.DailyMsq?.QueueTarget;
        var step = worker.StepResult;
        var status = step.ExecutorStatus;
        var references = command.Plan.Orchestration.RequiredRosterCharacters.Where(row =>
            DadRosterIdentity.SameAccount(row.AccountKey, participant.ManagedAccountKey) &&
            string.Equals(row.CharacterKey.Value, participant.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase) &&
            row.ContentId == participant.Character.ContentId).Take(2).ToList();
        var reference = references.Count == 1 ? references[0] : null;
        if (target == null || reference == null || string.IsNullOrWhiteSpace(command.RunId) || worker.ModuleId != DadModuleId.DailyMsq ||
            step.ModuleId != DadModuleId.DailyMsq || status.ModuleId != DadModuleId.DailyMsq ||
            !worker.IsTerminal || !worker.Success || !worker.EnteredDuty || worker.State != DadWorkerExecutionState.Completed ||
            !step.Success || status.IsActive || status.Status != DadRunStatus.Completed || status.DutyExitedWithoutCompletion ||
            !string.IsNullOrWhiteSpace(worker.FailureReason) || !string.IsNullOrWhiteSpace(step.FailureReason) ||
            !string.IsNullOrWhiteSpace(status.FailureReason) ||
            !string.Equals(participant.Character.CharacterKey, participant.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            command.CommandId != worker.CommandId || command.RunId != worker.RunId || command.Role != worker.Role ||
            command.RunId != command.Plan.Request.RequestId ||
            command.RunId != step.RunId || command.RunId != status.RunId ||
            !string.Equals(worker.WorkerSessionId.Value, participant.WorkerSessionId.Value, StringComparison.OrdinalIgnoreCase) ||
            status.StartedAtUtc is not { } started || status.DutyCompletedAtUtc is not { } completed ||
            status.CompletedAtUtc is not { Kind: DateTimeKind.Utc } settled || completed < started || settled < completed || settled > now ||
            !SamePeriod(started, completed, now))
            return false;
        return Insert(records, reference.AccountKey, reference.CharacterKey, reference.ContentId, target,
            completed, command.CommandId, DadDailyRouletteProof.CompletedDuty, now);
    }

    private static bool Insert(List<DadDailyRouletteCompletion> records, DadAccountKey account,
        DadCharacterKey character, ulong contentId, DadQueueTarget target, DateTime confirmed,
        string evidenceId, DadDailyRouletteProof proof, DateTime now)
    {
        if (!ValidIdentity(account, character, contentId, target) || string.IsNullOrWhiteSpace(evidenceId))
            return false;
        records.RemoveAll(row => !IsCurrent(row, now) ||
            (DadRosterIdentity.SameAccount(row.AccountKey, account) && row.ContentId == contentId && row.RouletteId == target.RouletteId));
        records.Add(new DadDailyRouletteCompletion
        {
            AccountKey = account, CharacterKey = character, ContentId = contentId,
            RouletteId = target.RouletteId, RouletteKey = target.Key,
            ResetBoundaryUtc = DadScheduleRules.GetDailyResetBoundaryUtc(now),
            ConfirmedAtUtc = confirmed, EvidenceId = evidenceId, Proof = proof,
            RewardTextProofVersion = proof == DadDailyRouletteProof.RewardReceived
                ? DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion : 0,
        });
        if (records.Count > MaximumRecords)
        {
            records.Sort(static (left, right) => right.ConfirmedAtUtc.CompareTo(left.ConfirmedAtUtc));
            records.RemoveRange(MaximumRecords, records.Count - MaximumRecords);
        }
        return true;
    }

    private static bool ValidIdentity(DadAccountKey account, DadCharacterKey character, ulong contentId, DadQueueTarget target)
        => !account.IsEmpty && !character.IsEmpty && contentId != 0 && target.Kind == DadQueueTargetKind.Roulette &&
           target.RouletteId is > 0 and <= byte.MaxValue && string.Equals(target.Key, $"ContentRoulette:{target.RouletteId}", StringComparison.OrdinalIgnoreCase);

    private static bool SamePeriod(DateTime started, DateTime confirmed, DateTime now)
        => started.Kind == DateTimeKind.Utc && confirmed.Kind == DateTimeKind.Utc && now.Kind == DateTimeKind.Utc &&
           started <= confirmed && confirmed <= now &&
           DadScheduleRules.GetDailyResetBoundaryUtc(started) == DadScheduleRules.GetDailyResetBoundaryUtc(now) &&
           DadScheduleRules.GetDailyResetBoundaryUtc(confirmed) == DadScheduleRules.GetDailyResetBoundaryUtc(now);

    private static bool IsCurrent(DadDailyRouletteCompletion? row, DateTime now)
        => row != null && row.ContentId != 0 && !row.AccountKey.IsEmpty && !row.CharacterKey.IsEmpty &&
           row.RouletteId is > 0 and <= byte.MaxValue && string.Equals(row.RouletteKey, $"ContentRoulette:{row.RouletteId}", StringComparison.OrdinalIgnoreCase) &&
           (row.Proof == DadDailyRouletteProof.CompletedDuty ||
            row.Proof == DadDailyRouletteProof.RewardReceived && row.RewardTextProofVersion == DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion) &&
           !string.IsNullOrWhiteSpace(row.EvidenceId) && row.ResetBoundaryUtc.Kind == DateTimeKind.Utc &&
           row.ResetBoundaryUtc == DadScheduleRules.GetDailyResetBoundaryUtc(now) &&
           SamePeriod(row.ResetBoundaryUtc, row.ConfirmedAtUtc, now);
}
