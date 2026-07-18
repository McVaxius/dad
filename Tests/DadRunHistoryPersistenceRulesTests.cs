using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRunHistoryPersistenceRulesTests
{
    [Fact]
    public void SnapshotRetainsVisibleSchedulerAndHistoryFields()
    {
        var completedAt = new DateTime(2026, 7, 17, 14, 0, 0, DateTimeKind.Utc);
        var source = HeavyResult("run-1", completedAt);

        var snapshot = DadRunHistoryPersistenceRules.CreateSnapshot(source);

        Assert.Equal(source.RequestId, snapshot.RequestId);
        Assert.Equal(source.Status, snapshot.Status);
        Assert.Equal(source.Phase, snapshot.Phase);
        Assert.Equal(source.Role, snapshot.Role);
        Assert.Equal(source.WorkerRole, snapshot.WorkerRole);
        Assert.Equal(source.AuthorityMode, snapshot.AuthorityMode);
        Assert.Equal(source.CancellationState, snapshot.CancellationState);
        Assert.Equal(source.ModuleId, snapshot.ModuleId);
        Assert.Equal(source.TransportMode, snapshot.TransportMode);
        Assert.Equal(source.LocalOnlyEnabled, snapshot.LocalOnlyEnabled);
        Assert.Equal(source.RequestedBy, snapshot.RequestedBy);
        Assert.Equal(source.RequestedTaskCount, snapshot.RequestedTaskCount);
        Assert.Equal(source.CompletedTaskCount, snapshot.CompletedTaskCount);
        Assert.Equal(source.ActiveTaskIndex, snapshot.ActiveTaskIndex);
        Assert.Equal(source.TotalTaskCount, snapshot.TotalTaskCount);
        Assert.Equal(source.ActiveTaskName, snapshot.ActiveTaskName);
        Assert.Equal(source.ActiveTaskStatus, snapshot.ActiveTaskStatus);
        Assert.Equal(source.BlockedReason, snapshot.BlockedReason);
        Assert.Equal(source.FailureReason, snapshot.FailureReason);
        Assert.Equal(source.Summary, snapshot.Summary);
        Assert.Equal(DadScheduleFailureKind.CoordinatorReloadAbandonment, snapshot.ScheduleFailureKind);
        Assert.Equal(completedAt, snapshot.CompletedAtUtc);
        Assert.Equal(2, snapshot.StopProgress.StartedRuns);
        Assert.Equal(1, snapshot.StopProgress.CompletedRuns);
        Assert.Equal("stop progress", snapshot.StopProgress.Summary);
        Assert.Equal("history warning", Assert.Single(snapshot.Warnings));
        var step = Assert.Single(snapshot.StepResults);
        Assert.Equal("queue", step.StepName);
        Assert.Equal("step failed", step.FailureReason);
        Assert.Equal(DadModuleId.PremadeDuty, Assert.Single(step.ModuleBlockers).ModuleId);
    }

    [Fact]
    public void SnapshotClearsRuntimeHeavyAndIdentityFieldsIncludingNestedExecutorState()
    {
        var snapshot = DadRunHistoryPersistenceRules.CreateSnapshot(HeavyResult("run-2", DateTime.UtcNow));

        Assert.Null(snapshot.Request);
        Assert.Empty(snapshot.Participants);
        Assert.Empty(snapshot.Leases);
        Assert.Empty(snapshot.LeaderClientInstanceId);
        Assert.True(snapshot.AuthorityWorkerSessionId.IsEmpty);
        Assert.Empty(snapshot.AuthorityEndpoint);
        Assert.Empty(snapshot.LocalClientInstanceId);
        Assert.True(snapshot.LocalWorkerSessionId.IsEmpty);
        Assert.Equal(DateTime.MinValue, snapshot.CurrentExecutorStatus.UpdatedAtUtc);
        Assert.False(snapshot.CurrentExecutorStatus.IsActive);
        Assert.Equal(DateTime.MinValue, Assert.Single(snapshot.StepResults).ExecutorStatus.UpdatedAtUtc);
        Assert.True(DadRunHistoryPersistenceRules.IsCompactSnapshot(snapshot));
    }

    [Fact]
    public void InsertingSnapshotKeepsNewestFiftyEntries()
    {
        var history = Enumerable.Range(0, DadRunHistoryPersistenceRules.MaximumEntries)
            .Select(index => DadRunHistoryPersistenceRules.CreateSnapshot(
                HeavyResult($"old-{index}", DateTime.UtcNow.AddMinutes(-index))))
            .ToList();

        DadRunHistoryPersistenceRules.InsertSnapshot(
            history,
            HeavyResult("newest", DateTime.UtcNow.AddMinutes(1)));

        Assert.Equal(DadRunHistoryPersistenceRules.MaximumEntries, history.Count);
        Assert.Equal("newest", history[0].RequestId);
        Assert.DoesNotContain(history, result => result.RequestId == "old-49");
        Assert.All(history, result => Assert.True(DadRunHistoryPersistenceRules.IsCompactSnapshot(result)));
    }

    [Fact]
    public void StartupCompactionChangesLegacyHistoryOnce()
    {
        var history = new List<DadRunResult>
        {
            HeavyResult("legacy", DateTime.UtcNow),
            DadRunHistoryPersistenceRules.CreateSnapshot(HeavyResult("already-compact", DateTime.UtcNow)),
        };

        Assert.True(DadRunHistoryPersistenceRules.CompactLegacyHistory(history));
        var compactReferences = history.ToArray();
        Assert.False(DadRunHistoryPersistenceRules.CompactLegacyHistory(history));
        Assert.Same(compactReferences[0], history[0]);
        Assert.Same(compactReferences[1], history[1]);
    }

    [Fact]
    public void RecoveredRunSnapshotRetainsReloadFailureForHistoryDisplay()
    {
        var recovered = HeavyResult("recovered", DateTime.UtcNow);
        recovered.Status = DadRunStatus.Failed;
        recovered.Phase = DadRunPhase.Finalizing;
        recovered.ScheduleFailureKind = DadScheduleFailureKind.CoordinatorReloadAbandonment;
        recovered.FailureReason = "Run abandoned by plugin reload; explicit restart required.";
        recovered.Summary = recovered.FailureReason;

        var history = new List<DadRunResult>();
        var snapshot = DadRunHistoryPersistenceRules.InsertSnapshot(history, recovered);

        Assert.Equal(DadRunStatus.Failed, snapshot.Status);
        Assert.Equal(DadRunPhase.Finalizing, snapshot.Phase);
        Assert.Equal(DadScheduleFailureKind.CoordinatorReloadAbandonment, snapshot.ScheduleFailureKind);
        Assert.Equal(recovered.FailureReason, snapshot.FailureReason);
        Assert.Equal(recovered.Summary, snapshot.Summary);
        Assert.NotNull(snapshot.CompletedAtUtc);
    }

    [Fact]
    public void SchedulerFallbackCanResolveCompactHistoryByRequestId()
    {
        var history = new List<DadRunResult>();
        DadRunHistoryPersistenceRules.InsertSnapshot(
            history,
            HeavyResult("scheduler-request", DateTime.UtcNow));

        var fallback = history.FirstOrDefault(result =>
            string.Equals(result.RequestId, "scheduler-request", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(fallback);
        Assert.Equal(DadRunStatus.Failed, fallback!.Status);
        Assert.Equal(DadScheduleFailureKind.CoordinatorReloadAbandonment, fallback.ScheduleFailureKind);
        Assert.Equal("failed", fallback.FailureReason);
        Assert.Equal("blocked", fallback.BlockedReason);
        Assert.Equal("summary", fallback.Summary);
    }

    private static DadRunResult HeavyResult(string requestId, DateTime completedAtUtc)
        => new()
        {
            RequestId = requestId,
            Status = DadRunStatus.Failed,
            Phase = DadRunPhase.InDutyOrTask,
            Role = DadOrchestrationRole.Leader,
            WorkerRole = DadWorkerRole.ServerDad,
            AuthorityMode = DadAuthorityMode.ServerDad,
            CancellationState = DadRunCancellationState.Acknowledged,
            ModuleId = DadModuleId.PremadeDuty,
            TransportMode = DadTransportMode.ServerHub,
            LocalOnlyEnabled = false,
            LeaderClientInstanceId = "leader-client",
            AuthorityWorkerSessionId = new DadWorkerSessionId("authority-session"),
            AuthorityEndpoint = "127.0.0.1:4647",
            LocalClientInstanceId = "local-client",
            LocalWorkerSessionId = new DadWorkerSessionId("local-session"),
            RequestedBy = "scheduler",
            RequestedTaskCount = 3,
            CompletedTaskCount = 1,
            ActiveTaskIndex = 2,
            TotalTaskCount = 3,
            ActiveTaskName = "queue",
            ActiveTaskStatus = "failed",
            BlockedReason = "blocked",
            FailureReason = "failed",
            Summary = "summary",
            ScheduleFailureKind = DadScheduleFailureKind.CoordinatorReloadAbandonment,
            Request = new DadRunRequest { RequestId = requestId, RequestedBy = "scheduler" },
            StopProgress = new DadRunStopProgress
            {
                StopPolicy = new DadRunStopPolicy { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 3 },
                StartedRuns = 2,
                CompletedRuns = 1,
                SafetyCap = 3,
                Summary = "stop progress",
            },
            CurrentExecutorStatus = new DadModuleExecutionStatusDto
            {
                RunId = requestId,
                ModuleId = DadModuleId.PremadeDuty,
                IsActive = true,
                UpdatedAtUtc = completedAtUtc,
                Summary = "runtime executor",
            },
            Participants = [new DadParticipantSnapshot { ClientInstanceId = "participant-client" }],
            Leases = [new DadParticipantLeaseRecord()],
            StepResults =
            [
                new DadRunStepResultDto
                {
                    RunId = requestId,
                    ModuleId = DadModuleId.PremadeDuty,
                    StepName = "queue",
                    Success = false,
                    FailureReason = "step failed",
                    ExecutorStatus = new DadModuleExecutionStatusDto
                    {
                        RunId = requestId,
                        ModuleId = DadModuleId.PremadeDuty,
                        IsActive = true,
                        UpdatedAtUtc = completedAtUtc,
                    },
                    ModuleBlockers = [new DadModuleBlockerDto { ModuleId = DadModuleId.PremadeDuty }],
                    ReportedAtUtc = completedAtUtc,
                },
            ],
            Warnings = ["history warning"],
            CompletedAtUtc = completedAtUtc,
        };
}
