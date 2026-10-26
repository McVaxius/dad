extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using M = DadRuntime::dad.Models;
using S = DadRuntime::dad.Services;

namespace dad.Tests;

public sealed class DadRestartCancellationRuntimeTests
{
    private static readonly DateTime OriginalStart = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("missing")]
    [InlineData("remote")]
    [InlineData("stale")]
    [InlineData("invalid-pid")]
    [InlineData("unreadable-at-capture")]
    [InlineData("already-dead-at-capture")]
    public void MissingOrUntrustedLifetimeEvidenceNeverReleasesCleanup(string scenario)
    {
        DateTime? started = scenario == "already-dead-at-capture" ? null : OriginalStart;
        var inaccessible = scenario == "unreadable-at-capture";
        var lifetime = new S.DadWorkerProcessLifetime("local", _ => inaccessible
            ? throw new UnauthorizedAccessException() : started);
        var worker = Participant();
        if (scenario == "remote") worker.MachineName = "another-machine";
        if (scenario == "stale") worker.State = M.DadParticipantState.Stale;
        if (scenario == "invalid-pid") worker.ProcessId = 0;
        if (scenario != "missing") lifetime.Observe(worker);
        inaccessible = false;
        started = null;
        Assert.False(lifetime.HasExited(worker.WorkerSessionId));
    }

    [Fact]
    public void OnlyOriginalProcessExitOrPidReuseRetiresAnObservedLifetime()
    {
        DateTime? started = OriginalStart;
        var unreadable = false;
        var lifetime = new S.DadWorkerProcessLifetime("local", _ => unreadable
            ? throw new UnauthorizedAccessException() : started);
        var worker = Participant();
        lifetime.Observe(worker);
        Assert.False(lifetime.HasExited(worker.WorkerSessionId)); // Also covers plugin reload in same PID.
        unreadable = true;
        Assert.False(lifetime.HasExited(worker.WorkerSessionId));
        unreadable = false;
        started = default(DateTime);
        Assert.False(lifetime.HasExited(worker.WorkerSessionId));
        started = OriginalStart.AddMinutes(2); // PID reused: not the observed original process.
        Assert.True(lifetime.HasExited(worker.WorkerSessionId));
        started = null;
        Assert.True(lifetime.HasExited(worker.WorkerSessionId));
        Assert.False(lifetime.HasExited(new("replacement-worker")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingProcessIdentityCannotReleaseAnOldSession(bool changedMachine)
    {
        DateTime? started = OriginalStart;
        var lifetime = new S.DadWorkerProcessLifetime("local", _ => started);
        var worker = Participant();
        lifetime.Observe(worker);
        if (changedMachine) worker.MachineName = "remote";
        else worker.ProcessId++;
        lifetime.Observe(worker);
        started = null;
        Assert.False(lifetime.HasExited(worker.WorkerSessionId));
    }

    [Fact]
    public void TransportStillRequiresTheExactOldRouteToBeOffline()
    {
        var (transport, worker) = ExitedTransport();
        var method = typeof(S.DadTransportService).GetMethod("HasWorkerProcessExited", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(transport, [worker])!);
        var presence = typeof(S.DadTransportService).GetField("presenceService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
        Set(presence, "<WorkerSessionId>k__BackingField", worker);
        Assert.False((bool)method.Invoke(transport, [worker])!);
    }

    [Theory]
    [InlineData("pendingTakeoverCancellations", "Slot", "UpdatePendingTakeoverCancellations")]
    [InlineData("pendingEarlyAssignmentCancellations", "WorkerSessionId", "UpdatePendingEarlyAssignmentCancellations")]
    [InlineData("pendingRewardProbeCancellations", "Request", "UpdatePendingRewardProbeCancellations")]
    public void RealSchedulerRetiresExactExitedProcessWithoutCallingReplacementOrNativeServices(
        string fieldName, string identityProperty, string updateMethod)
    {
        var (transport, worker) = ExitedTransport();
        var scheduler = new S.DadSchedulerService(new DadRuntime::dad.Configuration(), null!, null!, null!,
            null!, transport, null!, null!, DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var field = typeof(S.DadSchedulerService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dictionary = (IDictionary)field.GetValue(scheduler)!;
        var pending = Activator.CreateInstance(field.FieldType.GetGenericArguments()[1], nonPublic: true)!;
        object identity = identityProperty switch
        {
            "Slot" => new M.DadSchedulerSlotState { MatchedWorkerSessionId = worker, SlotId = "Slot4" },
            "Request" => new M.DadRouletteRewardProbeRequestDto { RouteWorkerSessionId = worker, OperationId = "old-probe" },
            _ => worker,
        };
        pending.GetType().GetProperty(identityProperty)!.SetValue(pending, identity);
        pending.GetType().GetProperty("CancellationDeadlineUtc")!.SetValue(pending, DateTime.UtcNow.AddMinutes(1));
        dictionary.Add("exact-old-cleanup", pending);
        typeof(S.DadSchedulerService).GetMethod(updateMethod)!.Invoke(scheduler, null);
        Assert.Empty(dictionary); // No pool, native presence, wake service or replacement route exists in this fixture.
    }

    [Fact]
    public void RetiringCoordinatorProcessWorkDoesNotFabricateAWorkerAcknowledgement()
    {
        var (transport, worker) = ExitedTransport();
        var coordinator = DadDutyExitRetryTeardownRuntimeTests.Fixture(true);
        Set(coordinator, "transportService", transport);
        var field = typeof(S.DadCoordinatorService).GetField("pendingCoordinatorCancellations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dictionary = (IDictionary)field.GetValue(coordinator)!;
        var pending = Activator.CreateInstance(field.FieldType.GetGenericArguments()[1], nonPublic: true)!;
        var target = Participant();
        target.WorkerSessionId = worker;
        pending.GetType().GetProperty("Target")!.SetValue(pending, target);
        pending.GetType().GetProperty("CancellationDeadlineUtc")!.SetValue(pending, DateTime.UtcNow.AddMinutes(1));
        dictionary.Add("old-worker", pending);
        typeof(S.DadCoordinatorService).GetMethod("UpdateCancellationCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, null);
        Assert.Empty(dictionary);
        Assert.False((bool)pending.GetType().GetProperty("WorkerAcknowledged")!.GetValue(pending)!);
        Assert.False((bool)pending.GetType().GetProperty("RunAcknowledged")!.GetValue(pending)!);
        Assert.Equal(M.DadRunCancellationState.Finalized, target.CancellationState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealStopAllPollCannotAcknowledgeWhileSchedulerOrCoordinatorCleanupRemains(bool coordinatorPending)
    {
        var log = DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>();
        var scheduler = new S.DadSchedulerService(new DadRuntime::dad.Configuration(), null!, null!, null!,
            null!, null!, null!, null!, log);
        var coordinator = DadDutyExitRetryTeardownRuntimeTests.Fixture(true);
        var pendingOwner = coordinatorPending ? (object)coordinator : scheduler;
        var pendingField = pendingOwner.GetType().GetField(coordinatorPending
            ? "pendingCoordinatorCancellations" : "pendingEarlyAssignmentCancellations",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (IDictionary)pendingField.GetValue(pendingOwner)!;
        var entry = Activator.CreateInstance(pendingField.FieldType.GetGenericArguments()[1], nonPublic: true)!;
        entry.GetType().GetProperty("NextAttemptUtc")!.SetValue(entry, DateTime.MaxValue);
        entry.GetType().GetProperty("DeadlineLogged")!.SetValue(entry, true);
        pending.Add("old-route", entry);

        var queue = (S.DadQueueExecutionService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadQueueExecutionService));
        Set(queue, "gatherPartyExecutor", RuntimeHelpers.GetUninitializedObject(typeof(S.DadGatherPartyExecutor)));
        var type = typeof(S.DadSchedulerService).Assembly.GetType("dad.Services.DadLocalLifecycleCleanup")!;
        var cleanup = RuntimeHelpers.GetUninitializedObject(type);
        Set(cleanup, "<SchedulerService>P", scheduler);
        Set(cleanup, "<RunCoordinatorService>P", coordinator);
        Set(cleanup, "<WakeTakeoverService>P", new S.DadWakeTakeoverService(null!));
        Set(cleanup, "<QueueExecutionService>P", queue);
        Set(cleanup, "<Log>P", log);
        var recordedField = type.GetField("localStopAllResults", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var recorded = (IDictionary)Activator.CreateInstance(recordedField.FieldType)!;
        recorded.Add("stop", new M.DadStopAllWorkerResult
        {
            OperationId = "stop", State = M.DadStopAllWorkerState.Expected, LocalCleanupCompleted = false,
        });
        recordedField.SetValue(cleanup, recorded);
        var method = type.GetMethod("RunLocalLifecycleCleanup")!;
        var request = new M.DadStopAllRequest { OperationId = "stop" };
        M.DadStopAllWorkerResult Poll() => (M.DadStopAllWorkerResult)method.Invoke(cleanup, [request])!;
        var first = Poll();
        Assert.False(first.LocalCleanupCompleted);
        Assert.Equal(M.DadStopAllWorkerState.Expected, first.State);
        Assert.Contains("cancellation cleanup is pending", first.Summary);
        Assert.Equal(first.Summary, Poll().Summary);
        pending.Clear();
        var complete = Poll();
        Assert.True(complete.LocalCleanupCompleted);
        Assert.Equal(M.DadStopAllWorkerState.Acknowledged, complete.State);
        Assert.DoesNotContain("cleanup is pending", complete.Summary);
        Assert.Equal(complete.Summary, Poll().Summary);
        // All broad-stop dependencies are deliberately absent; none may run on a cleanup poll.
    }

    [Fact]
    public void DefaultOsProbeDoesNotMistakeCurrentLiveProcessForAnExitedOne()
    {
        var lifetime = new S.DadWorkerProcessLifetime();
        var participant = Participant();
        participant.MachineName = Environment.MachineName;
        participant.ProcessId = Environment.ProcessId;
        lifetime.Observe(participant);
        Assert.False(lifetime.HasExited(participant.WorkerSessionId));
    }

    private static (S.DadTransportService, M.DadWorkerSessionId) ExitedTransport()
    {
        DateTime? started = OriginalStart;
        var lifetime = new S.DadWorkerProcessLifetime("local", _ => started);
        var worker = Participant();
        lifetime.Observe(worker);
        started = null;
        var presence = (S.DadPresenceService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadPresenceService));
        Set(presence, "<WorkerSessionId>k__BackingField", new M.DadWorkerSessionId("coordinator"));
        var transport = (S.DadTransportService)RuntimeHelpers.GetUninitializedObject(typeof(S.DadTransportService));
        Set(transport, "presenceService", presence);
        Set(transport, "configuration", new DadRuntime::dad.Configuration { RunAsServerDad = true });
        Set(transport, "workerProcessLifetimes", lifetime);
        var sessions = typeof(S.DadTransportService).GetField("serverSessions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sessions.SetValue(transport, Activator.CreateInstance(sessions.FieldType));
        return (transport, worker.WorkerSessionId);
    }

    private static M.DadParticipantSnapshot Participant() => new()
    {
        WorkerSessionId = new("old-worker"), MachineName = "local", ProcessId = 1234,
    };

    private static void Set(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    [Theory]
    [InlineData("Services/DadSchedulerService.cs", "HasWorkerProcessExited", 3)]
    [InlineData("Services/DadCoordinatorService.cs", "HasWorkerProcessExited", 1)]
    [InlineData("Services/DadLocalLifecycleCleanup.cs", "HasPendingCancellationCleanup", 2)]
    public void RestartCleanupAndStopAllMustUseProductionCompletionEvidence(string path, string expected, int minimum)
    {
        var source = Source(path);
        Assert.True(source.Split(expected, StringSplitOptions.None).Length - 1 >= minimum,
            $"{path} must use {expected} at every relevant cleanup boundary.");
    }

    private static string Source(string path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "dad.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, path));
        throw new InvalidOperationException("DAD root not found.");
    }
}
