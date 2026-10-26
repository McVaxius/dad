using System.Text.Json;
using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadDailyRouletteCompletionTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 14, 55, 0, DateTimeKind.Utc);
    private static DadQueueTarget Target => new() { Kind = DadQueueTargetKind.Roulette, RouletteId = 1, Key = "ContentRoulette:1" };
    private static DadRouletteRewardProbeRequestDto Probe() => new()
    {
        OperationId = "probe", SchedulerRunId = "scheduler", SlotId = "Slot2",
        RouteWorkerSessionId = new("remote-worker"), AccountKey = new("remote-account"),
        CharacterKey = new("Character@World"), CharacterContentId = 42,
        RouletteId = 1, RouletteKey = "ContentRoulette:1", RequestedAtUtc = Now.AddSeconds(-2),
    };

    private static List<DadDailyRouletteCompletion> Received()
    {
        var request = Probe();
        var response = DadRouletteRewardProbeResultDto.FromRequest(request, DadRouletteRewardProbeOutcome.Received,
            "native proof", Now.AddSeconds(-1), 1, 1);
        response.RewardTextProofVersion = DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion;
        List<DadDailyRouletteCompletion> records = [];
        Assert.True(DadDailyRouletteCompletionRules.RecordReward(records, request, response, Now));
        return records;
    }

    [Fact]
    public void ReceivedProofSurvivesSerializationAndExpiresAtExactReset()
    {
        var records = JsonSerializer.Deserialize<List<DadDailyRouletteCompletion>>(JsonSerializer.Serialize(Received()))!;
        Assert.True(Has(records, Now));
        Assert.True(Has(records, new(2026, 10, 21, 14, 59, 59, DateTimeKind.Utc)));
        Assert.False(Has(records, new(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc)));
        Assert.False(Has(records, Now.AddDays(1)));
    }

    private static bool Has(IEnumerable<DadDailyRouletteCompletion> records, DateTime now)
        => DadDailyRouletteCompletionRules.HasCurrentProof(records, new("remote-account"), new("Character@World"), 42, Target, now);

    [Theory]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("cid")]
    [InlineData("roulette")]
    [InlineData("key")]
    [InlineData("future")]
    [InlineData("unknown")]
    [InlineData("evidence")]
    [InlineData("reset")]
    [InlineData("legacy-visibility-proof")]
    public void ContradictoryOrUnprovenReceiptCannotSkip(string mismatch)
    {
        var records = Received();
        var row = records[0];
        switch (mismatch)
        {
            case "account": row.AccountKey = new("other"); break;
            case "character": row.CharacterKey = new("other"); break;
            case "cid": row.ContentId++; break;
            case "roulette": row.RouletteId++; break;
            case "key": row.RouletteKey = "ContentRoulette:3"; break;
            case "future": row.ConfirmedAtUtc = Now.AddSeconds(1); break;
            case "unknown": row.Proof = DadDailyRouletteProof.Unknown; break;
            case "evidence": row.EvidenceId = ""; break;
            case "reset": row.ResetBoundaryUtc = row.ResetBoundaryUtc.AddDays(-1); break;
            case "legacy-visibility-proof": row.RewardTextProofVersion = 0; break;
        }
        Assert.False(Has(records, Now));
    }

    [Theory]
    [InlineData(DadRouletteRewardProbeOutcome.Pending)]
    [InlineData(DadRouletteRewardProbeOutcome.NotReceived)]
    [InlineData(DadRouletteRewardProbeOutcome.Unknown)]
    public void OnlyReceivedCreatesRewardReceipt(DadRouletteRewardProbeOutcome outcome)
    {
        var request = Probe();
        var response = DadRouletteRewardProbeResultDto.FromRequest(request, outcome, "", Now, 0, 1);
        Assert.False(DadDailyRouletteCompletionRules.RecordReward([], request, response, Now));
    }

    [Fact]
    public void CrossResetOrContradictoryProbeNeverCreditsNewDay()
    {
        var request = Probe();
        var afterReset = Now.AddMinutes(6);
        var response = DadRouletteRewardProbeResultDto.FromRequest(request, DadRouletteRewardProbeOutcome.Received, "", afterReset, 1, 1);
        response.RewardTextProofVersion = DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion;
        Assert.False(DadDailyRouletteCompletionRules.RecordReward([], request, response, afterReset));
        response = DadRouletteRewardProbeResultDto.FromRequest(request, DadRouletteRewardProbeOutcome.Received, "", Now, 1, 1);
        response.RewardTextProofVersion = DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion;
        response.CharacterContentId++;
        Assert.False(DadDailyRouletteCompletionRules.RecordReward([], request, response, Now));
    }

    private static (DadWorkerExecutionCommand Command, DadWorkerExecutionStatus Worker, DadParticipantSnapshot Participant) Completed()
    {
        var reference = new DadRosterCharacterRef { AccountKey = new("remote-account"), CharacterKey = new("Character@World"), ContentId = 42 };
        var command = new DadWorkerExecutionCommand
        {
            RunId = "run", CommandId = "command", Role = DadWorkerExecutionRole.Participant,
            Plan = new() { Request = new() { RequestId = "run", DailyMsq = new() { QueueTarget = Target } },
                Orchestration = new() { RequiredRosterCharacters = [reference] } },
        };
        var worker = new DadWorkerExecutionStatus
        {
            RunId = "run", CommandId = "command", WorkerSessionId = new("worker"), Role = command.Role,
            ModuleId = DadModuleId.DailyMsq, State = DadWorkerExecutionState.Completed, EnteredDuty = true, IsTerminal = true, Success = true,
            StepResult = new()
            {
                RunId = "run", ModuleId = DadModuleId.DailyMsq, Success = true,
                ExecutorStatus = new() { RunId = "run", ModuleId = DadModuleId.DailyMsq, Status = DadRunStatus.Completed,
                    StartedAtUtc = Now.AddMinutes(-10), DutyCompletedAtUtc = Now.AddSeconds(-10), CompletedAtUtc = Now.AddSeconds(-1) },
            },
        };
        var participant = new DadParticipantSnapshot { WorkerSessionId = new("worker"), ManagedAccountKey = reference.AccountKey,
            ActiveCharacterKey = reference.CharacterKey, Character = new() { ContentId = 42, CharacterKey = reference.CharacterKey.Value } };
        return (command, worker, participant);
    }

    [Fact]
    public void SuccessfulRemoteWorkerWithOwnDutyWitnessCreatesRestartReceipt()
    {
        var (command, worker, participant) = Completed();
        List<DadDailyRouletteCompletion> records = [];
        Assert.True(DadDailyRouletteCompletionRules.RecordCompletedWorker(records, command, worker, participant, Now));
        Assert.Equal(DadDailyRouletteProof.CompletedDuty, records.Single().Proof);
        Assert.True(Has(records, Now));
        Assert.NotNull(worker.StepResult.ExecutorStatus.Clone().DutyCompletedAtUtc);
    }

    [Theory]
    [InlineData("witness")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("active")]
    [InlineData("not-entered")]
    [InlineData("wrong-worker")]
    [InlineData("wrong-cid")]
    [InlineData("wrong-char")]
    [InlineData("wrong-command")]
    [InlineData("wrong-run")]
    [InlineData("empty-run")]
    [InlineData("premade")]
    [InlineData("previous-period")]
    [InlineData("cross-reset")]
    [InlineData("future")]
    public void StartedFailedAbandonedOldOrUnrelatedWorkNeverCreditsDaily(string mismatch)
    {
        var (command, worker, participant) = Completed();
        var status = worker.StepResult.ExecutorStatus;
        switch (mismatch)
        {
            case "witness": status.DutyCompletedAtUtc = null; break;
            case "failed": worker.Success = false; break;
            case "cancelled": worker.State = DadWorkerExecutionState.Cancelled; break;
            case "active": status.IsActive = true; break;
            case "not-entered": worker.EnteredDuty = false; break;
            case "wrong-worker": worker.WorkerSessionId = new("other"); break;
            case "wrong-cid": participant.Character.ContentId++; break;
            case "wrong-char": participant.ActiveCharacterKey = new("other"); break;
            case "wrong-command": worker.CommandId = "other"; break;
            case "wrong-run": worker.RunId = "other"; break;
            case "empty-run": command.RunId = worker.RunId = worker.StepResult.RunId = status.RunId = command.Plan.Request.RequestId = ""; break;
            case "premade": command.Plan.Request.DailyMsq = null; break;
            case "previous-period": status.StartedAtUtc = Now.AddDays(-1); status.DutyCompletedAtUtc = Now.AddDays(-1); break;
            case "cross-reset": status.StartedAtUtc = Now.AddDays(-1); break;
            case "future": status.DutyCompletedAtUtc = Now.AddMinutes(1); break;
        }
        Assert.False(DadDailyRouletteCompletionRules.RecordCompletedWorker([], command, worker, participant, Now));
    }

    [Fact]
    public void DuplicateFrozenIdentityCannotCreateReceiptAndNullLegacyRowsCannotProveCompletion()
    {
        var (command, worker, participant) = Completed();
        command.Plan.Orchestration.RequiredRosterCharacters.Add(command.Plan.Orchestration.RequiredRosterCharacters[0].Clone());
        Assert.False(DadDailyRouletteCompletionRules.RecordCompletedWorker([], command, worker, participant, Now));
        Assert.False(Has([null!], Now));
    }

    [Fact]
    public void ReceiptsDeduplicateAndCannotFillLedgerIndefinitely()
    {
        var records = Received();
        for (var i = 0; i < 5000; i++)
        {
            var request = Probe();
            request.CharacterContentId = (ulong)i + 100;
            var response = DadRouletteRewardProbeResultDto.FromRequest(request, DadRouletteRewardProbeOutcome.Received, "", Now, 1, 1);
            response.RewardTextProofVersion = DadRouletteRewardProbeIdentityRules.CurrentRewardTextProofVersion;
            Assert.True(DadDailyRouletteCompletionRules.RecordReward(records, request, response, Now));
            Assert.True(DadDailyRouletteCompletionRules.RecordReward(records, request, response, Now));
        }
        Assert.Equal(DadDailyRouletteCompletionRules.MaximumRecords, records.Count);
        Assert.Equal(records.Count, records.Select(row => row.ContentId).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void OldOrUnknownWorkerCannotPersistVisibilityOnlyReceivedReply(int version)
    {
        var request = Probe();
        var response = DadRouletteRewardProbeResultDto.FromRequest(request, DadRouletteRewardProbeOutcome.Received, "old visibility proof", Now, 1, 1);
        response.RewardTextProofVersion = version;
        Assert.False(DadRouletteRewardProbeIdentityRules.HasCurrentReceivedTextProof(response));
        Assert.False(DadDailyRouletteCompletionRules.RecordReward([], request, response, Now));
    }
}
