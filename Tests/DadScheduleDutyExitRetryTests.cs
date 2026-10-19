using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadScheduleDutyExitRetryTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EligibleAbandonedRunMustSettleItsPartyBeforeRetryCanBeReserved()
    {
        var run = FailedRun();
        run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = false;
        Assert.True(DadScheduleDutyExitRetryRules.RequiresPartyTeardownBeforeRetry(State(), run));
        Assert.False(DadScheduleDutyExitRetryRules.CanReserve(State(), run));
        run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = true;
        Assert.True(DadScheduleDutyExitRetryRules.CanReserve(State(), run));
        run.Status = DadRunStatus.Cancelled;
        Assert.False(DadScheduleDutyExitRetryRules.RequiresPartyTeardownBeforeRetry(State(), run));
    }

    [Fact]
    public void PersistedRetryWithoutTeardownProofCannotResumeOrReachSkipPreflight()
    {
        var state = Reserved();
        state.DutyExitRetrySource!.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = false;
        Assert.Equal(DadDutyExitRetryAction.Block, Ready(state, Live()).Action);
    }

    [Fact]
    public void ReservesOnlyOneAttemptWithoutCompletingOrAdvancingTheFailedEntry()
    {
        var run = FailedRun();
        var original = State();
        var retry = DadScheduleDutyExitRetryRules.Reserve(original, run, Now);
        Assert.Equal(0, original.DutyExitRetryCount);
        Assert.Equal(1, retry.DutyExitRetryCount);
        Assert.Equal(original.CurrentEntryIndex, retry.CurrentEntryIndex);
        Assert.Equal(original.RepeatIteration, retry.RepeatIteration);
        Assert.Equal(0, retry.CompletedEntryExecutions);
        Assert.Equal(0, retry.SkippedEntryExecutions);
        Assert.Equal(1, retry.DutyExitRetrySource!.Request!.StopPolicy.SafetyCap);
        Assert.Equal(1, retry.DutyExitRetrySource.Request.StopPolicy.AfterRuns);
        Assert.False(DadScheduleDutyExitRetryRules.CanReserve(retry, run));
        run.Participants.Clear();
        Assert.Equal(4, retry.DutyExitRetrySource.Participants.Count);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("completed")]
    [InlineData("rejected")]
    [InlineData("generic-timeout")]
    [InlineData("no-typed-evidence")]
    [InlineData("no-party-teardown-proof")]
    [InlineData("unknown-leader")]
    [InlineData("reload")]
    [InlineData("wrong-run")]
    [InlineData("already-completed-a-run")]
    [InlineData("missing-worker")]
    [InlineData("duplicate-worker")]
    [InlineData("unknown-content-id")]
    [InlineData("shared-identity")]
    [InlineData("multiple-tasks")]
    [InlineData("unsupported-module")]
    [InlineData("local-authority")]
    public void UnsafeOrUnrelatedFailuresNeverReserveAutomaticRetry(string scenario)
    {
        var run = FailedRun();
        switch (scenario)
        {
            case "cancel": run.Status = DadRunStatus.Cancelled; break;
            case "completed": run.Status = DadRunStatus.Completed; break;
            case "rejected": run.Status = DadRunStatus.Rejected; break;
            case "generic-timeout": run.Status = DadRunStatus.TimedOut; break;
            case "no-typed-evidence": run.CurrentExecutorStatus.DutyExitedWithoutCompletion = false; break;
            case "no-party-teardown-proof": run.CurrentExecutorStatus.DutyExitPartyTeardownCompleted = false; break;
            case "unknown-leader": run.ScheduleFailureKind = DadScheduleFailureKind.MissingOrUnknownLeaderState; break;
            case "reload": run.ScheduleFailureKind = DadScheduleFailureKind.CoordinatorReloadAbandonment; break;
            case "wrong-run": run.RequestId = "other"; break;
            case "already-completed-a-run": run.StopProgress.CompletedRuns = 1; break;
            case "missing-worker": run.Participants.RemoveAt(3); break;
            case "duplicate-worker": run.Participants[3].WorkerSessionId = run.Participants[0].WorkerSessionId; break;
            case "unknown-content-id": run.Request!.Orchestration.RequiredRosterCharacters[0].ContentId = 0; break;
            case "shared-identity": run.Request!.Orchestration.RequiredRosterCharacters[0].SharedIdentityToken = "opaque"; break;
            case "multiple-tasks": run.Request!.DutySupport = new(); break;
            case "unsupported-module": run.ModuleId = DadModuleId.GatherParty; break;
            case "local-authority": run.Request!.Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly; break;
        }
        Assert.False(DadScheduleDutyExitRetryRules.CanReserve(State(), run));
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("admission")]
    [InlineData("disconnected")]
    [InlineData("in-duty")]
    [InlineData("loading")]
    [InlineData("ar-busy")]
    [InlineData("external-owner")]
    [InlineData("stale")]
    [InlineData("pre-failure-heartbeat")]
    [InlineData("future-heartbeat")]
    public void WaitsForEveryOriginalClientAndFreshSafeProof(string scenario)
    {
        var state = Reserved();
        var live = Live();
        switch (scenario)
        {
            case "disconnected": live.RemoveAt(3); break;
            case "in-duty": live[3].WorldReadyStable = false; break;
            case "loading": live[3].IsAvailable = false; break;
            case "ar-busy": live[3].AutoRetainerBusy = true; break;
            case "external-owner": live[3].ExternalAutomationHeld = true; break;
            case "stale": live[3].LastHeartbeatUtc = Now.AddMinutes(-1); break;
            case "pre-failure-heartbeat": live[3].LastHeartbeatUtc = Now; break;
            case "future-heartbeat": live[3].LastHeartbeatUtc = Now.AddMinutes(1); break;
        }
        var decision = DadScheduleDutyExitRetryRules.EvaluateReadiness(state, live,
            scenario == "cleanup", scenario == "admission" ? "pending coordinator cancellation" : "",
            Now.AddSeconds(2), TimeSpan.FromSeconds(12));
        Assert.Equal(DadDutyExitRetryAction.Wait, decision.Action);
        Assert.Equal(1, state.DutyExitRetryCount);
        Assert.Equal(0, state.CompletedEntryExecutions);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("account")]
    [InlineData("content-id")]
    [InlineData("other-run")]
    [InlineData("ambiguous")]
    public void ContradictoryFreshIdentityBlocksRatherThanReloggingOrRebinding(string scenario)
    {
        var live = Live();
        switch (scenario)
        {
            case "character": live[0].ActiveCharacterKey = new("other"); break;
            case "account": live[0].ManagedAccountKey = new("other"); break;
            case "content-id": live[0].Character.ContentId++; break;
            case "other-run": live[0].RunId = "other"; break;
            case "ambiguous": live.Add(live[0].Clone()); break;
        }
        Assert.Equal(DadDutyExitRetryAction.Block, Ready(Reserved(), live).Action);
    }

    [Fact]
    public void AllFourClientsMustBeReadyAndRecoveryWaitIsBounded()
    {
        Assert.Equal(DadDutyExitRetryAction.Retry, Ready(Reserved(), Live()).Action);
        Assert.Equal(DadDutyExitRetryAction.Block,
            DadScheduleDutyExitRetryRules.EvaluateReadiness(Reserved(), [], true, "pending",
                Now.AddMinutes(5), TimeSpan.FromSeconds(12)).Action);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("job")]
    [InlineData("leader")]
    [InlineData("duty")]
    [InlineData("sync")]
    [InlineData("authority")]
    [InlineData("extra-task")]
    public void RetryPreviewCannotSubstituteAnotherRosterDutyJobOrAuthority(string scenario)
    {
        var expected = FailedRun().Request!;
        var candidate = expected.Clone();
        Assert.True(DadScheduleDutyExitRetryRules.SameSelection(expected, candidate));
        switch (scenario)
        {
            case "character": candidate.Orchestration.RequiredRosterCharacters[1].CharacterKey = new("other"); break;
            case "job": candidate.Orchestration.RequiredRosterCharacters[1].RequiredJobId = 27; break;
            case "leader": candidate.Orchestration.PreferredLeaderCharacterKey = new("other"); break;
            case "duty": candidate.PremadeDuty!.ContentFinderConditionId++; break;
            case "sync": candidate.PremadeDuty!.Unsynced = true; break;
            case "authority": candidate.Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly; break;
            case "extra-task": candidate.Msq = new(); break;
        }
        Assert.False(DadScheduleDutyExitRetryRules.SameSelection(expected, candidate));
    }

    [Fact]
    public void DailyRouletteRetainsExactQueueTargetAndFreshRequestIdsAreAllowed()
    {
        var run = FailedRun();
        run.ModuleId = DadModuleId.DailyMsq;
        run.Request!.PremadeDuty = null;
        run.Request.DailyMsq = new() { QueueTarget = new() { Kind = DadQueueTargetKind.Roulette, RouletteId = 9 } };
        Assert.True(DadScheduleDutyExitRetryRules.CanReserve(State(), run));
        var candidate = run.Request.Clone();
        candidate.RequestId = "fresh-request";
        candidate.RequestedAtUtc = Now.AddSeconds(2);
        Assert.True(DadScheduleDutyExitRetryRules.SameSelection(run.Request, candidate));
        candidate.DailyMsq!.QueueTarget.RouletteId++;
        Assert.False(DadScheduleDutyExitRetryRules.SameSelection(run.Request, candidate));
    }

    [Fact]
    public void BudgetAndProvenanceSurviveCloningSerializationHistoryAndExplicitResume()
    {
        var state = Reserved();
        var loaded = JsonSerializer.Deserialize<DadScheduleRunState>(JsonSerializer.Serialize(state))!;
        Assert.Equal(1, loaded.DutyExitRetryCount);
        Assert.Equal(Now, loaded.DutyExitRetryPendingSinceUtc);
        Assert.Equal(4, loaded.Clone().DutyExitRetrySource!.Participants.Count);
        Assert.True(loaded.DutyExitRetrySource!.CurrentExecutorStatus.DutyExitedWithoutCompletion);
        Assert.True(loaded.DutyExitRetrySource.CurrentExecutorStatus.DutyExitPartyTeardownCompleted);
        var failed = DadScheduleRules.BlockRun(loaded, "retry failed", Now.AddMinutes(1), DadScheduleFailureKind.EntryTerminalFailure).ToResult(false).Clone();
        var schedule = new DadScheduleDefinition
        {
            ScheduleId = "schedule", Revision = 1,
            Entries = [new() { EntryId = "entry", GroupId = "preset" }],
        };
        Assert.True(DadScheduleRules.TryCreateRetryState(failed, schedule, "operator", Now.AddMinutes(2), out var resumed, out var blocker), blocker);
        Assert.Equal(1, resumed.DutyExitRetryCount);
        Assert.Null(resumed.DutyExitRetryPendingSinceUtc);
        Assert.False(DadScheduleDutyExitRetryRules.CanReserve(resumed, FailedRun()));
    }

    [Theory]
    [InlineData(15, 15, false)]
    [InlineData(16, 15, false)]
    [InlineData(16, 16, true)]
    [InlineData(19, 18, true)]
    public void FreshLevelPreflightSkipsOnlyWhenBothArcanistsMeetSixteen(int first, int second, bool skip)
    {
        var source = Reserved();
        var group = new DadPlannerGroup
        {
            Slots = Enumerable.Range(1, 2).Select(i => new DadPlannerGroupSlot
            {
                SlotId = $"Slot{i}", RequiredAccountKey = new($"account-{i}"),
                RequiredCharacterKey = new($"character-{i}"), RequiredJobId = 26, LevelSeekTarget = 16,
            }).ToList(),
        };
        var preview = new DadPlannerRunRequestPreview { PlannerPreview = new DadActivityPreset
        {
            AvailableCharacters = new[] { first, second }.Select((level, i) => new DadAcquiredCharacter
            {
                AccountId = $"account-{i + 1}", CharacterKey = $"character-{i + 1}", JobLevels = new() { [26] = level },
            }).ToList(),
        } };
        Assert.Equal(skip, DadLevelSeekEvaluationRules.EvaluatePlannerPreflight(group, preview).ShouldSkip);
        Assert.Equal(0, source.CompletedEntryExecutions);
        if (!skip) return;
        var advanced = DadScheduleRules.AdvanceAfterEntry(source,
            [new() { EntryId = "entry", GroupId = "preset" }, new() { EntryId = "next", GroupId = "next" }],
            true, "all level targets met", Now.AddSeconds(3), entrySkipped: true);
        Assert.Equal(1, advanced.CompletedEntryExecutions);
        Assert.Equal(1, advanced.SkippedEntryExecutions);
        Assert.Equal(1, advanced.CurrentEntryIndex);
        Assert.Equal(0, advanced.DutyExitRetryCount);
        Assert.Null(advanced.DutyExitRetrySource);
    }

    private static DadDutyExitRetryDecision Ready(DadScheduleRunState state, List<DadParticipantSnapshot> live)
        => DadScheduleDutyExitRetryRules.EvaluateReadiness(state, live, false, "", Now.AddSeconds(2), TimeSpan.FromSeconds(12));
    private static DadScheduleRunState Reserved() => DadScheduleDutyExitRetryRules.Reserve(State(), FailedRun(), Now);
    private static DadScheduleRunState State() => new()
    {
        RunId = "schedule-run", ScheduleId = "schedule", ScheduleRevisionAtStart = 1,
        Status = DadScheduleRunStatus.Running, Phase = DadScheduleRunPhase.WaitingForDadRun,
        CurrentEntryId = "entry", CurrentGroupId = "preset", ActivePlannerRequestId = "failed-run", TotalEntryExecutions = 2,
    };
    private static List<DadParticipantSnapshot> Live() => FailedRun().Participants.Select(row =>
    {
        row.IsAvailable = row.WorldReadyStable = row.PostArReady = true;
        row.LastHeartbeatUtc = Now.AddSeconds(1);
        return row;
    }).ToList();
    private static DadRunResult FailedRun()
    {
        var roster = Enumerable.Range(1, 4).Select(i => new DadRosterCharacterRef
        {
            AccountKey = new($"account-{i}"), CharacterKey = new($"character-{i}"), ContentId = (ulong)i,
            RequiredJobId = i <= 2 ? 26u : null,
        }).ToList();
        return new()
        {
            RequestId = "failed-run", Status = DadRunStatus.PartialFailure, ModuleId = DadModuleId.PremadeDuty,
            ScheduleFailureKind = DadScheduleFailureKind.EntryTerminalFailure, CompletedAtUtc = Now,
            CurrentExecutorStatus = new() { DutyExitedWithoutCompletion = true, DutyExitPartyTeardownCompleted = true },
            Request = new()
            {
                RequestId = "failed-run", PremadeDuty = new() { ContentFinderConditionId = 4 },
                StopPolicy = new() { AfterRuns = 1, SafetyCap = 1 },
                Orchestration = new() { ModuleTarget = DadModuleId.PremadeDuty, RequiredRosterCharacters = roster },
            },
            Participants = roster.Select((row, i) => new DadParticipantSnapshot
            {
                AssignedSlotId = $"Slot{i + 1}", WorkerSessionId = new($"worker-{i + 1}"),
                ManagedAccountKey = row.AccountKey, ActiveCharacterKey = row.CharacterKey,
                Character = new() { CharacterKey = row.CharacterKey.Value, ContentId = row.ContentId },
            }).ToList(),
        };
    }
}
