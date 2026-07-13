using System.Text.Json;
using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadSchedulerSubmissionRulesTests
{
    [Fact]
    public void CancellationCleanupBlocksImmediateRetryAndQueueStartUntilAcknowledged()
    {
        var cleanup = new DadScheduledCrewJob
        {
            JobId = "cancelled-job",
            GroupId = "group-a",
            StatusSummary = "Cancellation cleanup pending for scheduler Job ID cancelled-job.",
        };

        var duplicate = DadSchedulerSubmissionRules.FindDuplicate(
            "group-a",
            activeJob: null,
            activeState: false,
            pendingJobs: [],
            cancellationCleanupJobs: [cleanup]);

        Assert.NotNull(duplicate);
        Assert.Equal(DadSchedulerEnqueueDisposition.AlreadyPending, duplicate.Disposition);
        Assert.Equal("cancelled-job", duplicate.Job.JobId);
        Assert.False(DadSchedulerSubmissionRules.CanStartNextQueuedJob(cancellationCleanupPending: true));

        Assert.Null(DadSchedulerSubmissionRules.FindDuplicate(
            "group-a",
            activeJob: null,
            activeState: false,
            pendingJobs: [],
            cancellationCleanupJobs: []));
        Assert.True(DadSchedulerSubmissionRules.CanStartNextQueuedJob(cancellationCleanupPending: false));
    }

    [Fact]
    public void ImmediateTerminalResultIsCorrelatedByExactJobId()
    {
        var snapshot = new DadSchedulerQueueSnapshot
        {
            RecentResults =
            [
                Result("another-job", DadSchedulerPresetPhase.Blocked, "Unrelated blocker."),
                Result("submitted-job", DadSchedulerPresetPhase.Blocked, "Static authority failed."),
            ],
        };

        var disposition = DadSchedulerSubmissionRules.ResolveAfterUpdate(
            DadSchedulerEnqueueDisposition.Added,
            "submitted-job",
            snapshot);
        var feedback = DadSchedulerSubmissionRules.BuildFeedback(
            disposition,
            new DadScheduledCrewJob { JobId = "submitted-job", PresetName = "Preset" },
            snapshot);

        Assert.Equal(DadSchedulerEnqueueDisposition.TerminalBlocked, disposition);
        Assert.Contains("Job ID submitted-job", feedback, StringComparison.Ordinal);
        Assert.Contains("Static authority failed", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated blocker", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateDispositionsRetainExistingActiveOrPendingJob()
    {
        var job = new DadScheduledCrewJob
        {
            JobId = "existing-job",
            StatusSummary = "Already queued.",
        };
        var active = new DadSchedulerQueueSnapshot
        {
            ActiveJob = job,
            ActiveState = new DadSchedulerPresetState
            {
                JobId = job.JobId,
                Phase = DadSchedulerPresetPhase.WaitingForHeartbeat,
                Summary = "Waiting for coordinator character.",
            },
        };
        var pending = new DadSchedulerQueueSnapshot { PendingJobs = [job] };

        Assert.Equal(
            DadSchedulerEnqueueDisposition.AlreadyActive,
            DadSchedulerSubmissionRules.ResolveAfterUpdate(
                DadSchedulerEnqueueDisposition.AlreadyActive,
                job.JobId,
                active));
        Assert.Contains(
            "WaitingForHeartbeat",
            DadSchedulerSubmissionRules.BuildFeedback(DadSchedulerEnqueueDisposition.AlreadyActive, job, active),
            StringComparison.Ordinal);
        Assert.Contains(
            "already pending",
            DadSchedulerSubmissionRules.BuildFeedback(DadSchedulerEnqueueDisposition.AlreadyPending, job, pending),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalDispositionDoesNotChangeQueueWireShape()
    {
        using var document = JsonDocument.Parse(DadIpcJson.Serialize(new DadSchedulerQueueSnapshot()));
        var properties = document.RootElement.EnumerateObject().Select(static property => property.Name).ToList();

        Assert.Equal(
            ["generatedAtUtc", "summary", "activeQueueOwner", "activeJob", "pendingJobs", "recentResults", "activeState"],
            properties);
        Assert.DoesNotContain(properties, static property =>
            property.Contains("disposition", StringComparison.OrdinalIgnoreCase));
    }

    private static DadScheduledCrewJobResult Result(
        string jobId,
        DadSchedulerPresetPhase phase,
        string reason)
        => new()
        {
            JobId = jobId,
            FinalPhase = phase,
            Summary = reason,
            BlockedReason = reason,
        };
}
