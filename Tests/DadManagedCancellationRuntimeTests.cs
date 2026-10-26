extern alias DadRuntime;
extern alias DalamudApi;

using System.Reflection;
using System.Runtime.CompilerServices;
using DalamudApi::Dalamud.Plugin.Services;
using Xunit;
using C = DadRuntime::dad.Services.DadCoordinatorService;
using M = DadRuntime::dad.Models;

namespace dad.Tests;

public sealed class DadManagedCancellationRuntimeTests
{
    [Fact]
    public void OperatorCancelMustEnterManagedCleanupBeforeRevokingWorkerRunContext()
    {
        var source = Source("Services/DadCoordinatorService.cs");
        var start = source.IndexOf("public DadRunResult CancelActiveRun()", StringComparison.Ordinal);
        var end = source.IndexOf("public DadRunResult CancelAllLocal(", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.Contains("TryBeginManagedCancellation", method);
        Assert.True(method.IndexOf("TryBeginManagedCancellation", StringComparison.Ordinal) <
                    method.IndexOf("presenceService.HandleCancelRun", StringComparison.Ordinal));
    }

    [Fact]
    public void ScheduleCancelMustNotifyTheExactAcceptedPlannerRun()
    {
        var source = Source("Services/DadSchedulerService.cs");
        var start = source.IndexOf("public bool CancelScheduleRun(string runId", StringComparison.Ordinal);
        var end = source.IndexOf("public DadScheduleRetryResult", start, StringComparison.Ordinal);
        Assert.Contains("cancelScheduledPlannerRun", source[start..end]);
        Assert.Contains("ConfigureScheduledRunCancellation", Source("Plugin.cs"));
        Assert.Contains("ConfigureScheduledRunCancellation", Source("Headless/RuntimeNode.cs"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealTeardownRetainsRunUntilEveryExactFollowerSucceeded(bool success)
    {
        var coordinator = DadDutyExitRetryTeardownRuntimeTests.Fixture(success);
        var original = Get(coordinator, "activePlan");
        var pending = InstallPending(coordinator);
        Invoke(coordinator, "AdvanceManagedCancellationTeardown");
        Assert.Equal(success, Property<bool>(pending, "PartySettled"));
        Assert.Equal(!success, Property<bool>(pending, "Failed"));
        Assert.Same(original, Get(coordinator, "activePlan"));
        Assert.False(coordinator.CurrentResult.IsTerminal);
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public void PendingWorkerAcknowledgementCannotStartPartyTeardownOrReleaseContext()
    {
        var coordinator = DadDutyExitRetryTeardownRuntimeTests.Fixture(true);
        var original = Get(coordinator, "activePlan");
        var pending = InstallPending(coordinator);
        pending.GetType().GetProperty("NextLocalAttemptUtc")!.SetValue(pending, DateTime.MaxValue);
        Invoke(coordinator, "UpdateManagedCancellation");
        Assert.False(Property<bool>(pending, "PartySettled"));
        Assert.Same(original, Get(coordinator, "activePlan"));
        Assert.Contains("worker stop acknowledgements", coordinator.CurrentResult.BlockedReason);
    }

    [Fact]
    public void RepeatedCancelKeepsSameCleanupAttemptAndCannotRearmIt()
    {
        var coordinator = DadDutyExitRetryTeardownRuntimeTests.Fixture(true);
        var pending = InstallPending(coordinator);
        object?[] args = ["Repeated Cancel", null];
        Assert.True((bool)Method("TryBeginManagedCancellation").Invoke(coordinator, args)!);
        Assert.Same(pending, Get(coordinator, "managedCancellation"));
        Assert.Equal("Cancelled by operator.", Property<string>(pending, "Reason"));
        Assert.False(Property<bool>(pending, "PartySettled"));
    }

    [Theory]
    [InlineData(M.DadRunStatus.Completed)]
    [InlineData(M.DadRunStatus.Failed)]
    [InlineData(M.DadRunStatus.Cancelled)]
    public void LateFinalizationCannotDiscardAnUnsettledCancellation(M.DadRunStatus status)
    {
        var coordinator = DadDutyExitRetryTeardownRuntimeTests.Fixture(true);
        var original = Get(coordinator, "activePlan");
        var pending = InstallPending(coordinator);
        Invoke(coordinator, "FinalizeRun", status, "late result", "unproven cleanup", false, false);
        Assert.True(Property<bool>(pending, "Failed"));
        Assert.Same(original, Get(coordinator, "activePlan"));
        Assert.False(coordinator.CurrentResult.IsTerminal);
        Assert.True(coordinator.IsBusy);
        Assert.Equal(M.DadRunCancellationState.Cancelling, coordinator.CurrentResult.CancellationState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealScheduleCancelNotifiesItsExactPlannerRequestOnlyOnce(bool cursorHasRequest)
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var scheduler = new DadRuntime::dad.Services.DadSchedulerService(config, null!, null!, null!, null!,
            null!, null!, null!, DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        config.ActiveScheduleRun = new()
        {
            RunId = "schedule-run", ScheduleId = "schedule", Status = M.DadScheduleRunStatus.Running,
            Phase = M.DadScheduleRunPhase.WaitingForDadRun, ActiveSchedulerJobId = "job",
            ActivePlannerRequestId = cursorHasRequest ? "exact-run" : "",
        };
        var state = new M.DadSchedulerPresetState { JobId = "job", PlannerRequestId = "exact-run" };
        typeof(DadRuntime::dad.Services.DadSchedulerService).GetField("currentState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scheduler, state);
        var calls = new List<string>();
        typeof(DadRuntime::dad.Services.DadSchedulerService).GetMethod("ConfigureScheduledRunCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [new Action<string>(calls.Add), new Func<string, bool>(_ => false)]);
        Assert.False(scheduler.CancelScheduleRun("different-run", "Cancel"));
        Assert.Empty(calls);
        Assert.True(scheduler.CancelScheduleRun("schedule-run", "Cancel"));
        Assert.Equal("exact-run", Assert.Single(calls));
        Assert.False(scheduler.CancelScheduleRun("schedule-run", "Cancel again"));
        Assert.Single(calls);
    }

    [Theory]
    [InlineData("PendingTakeoverCancellation", "pendingTakeoverCancellations", "PlannerRunId", "UpdatePendingTakeoverCancellations")]
    [InlineData("PendingEarlyAssignmentCancellation", "pendingEarlyAssignmentCancellations", "RunId", "UpdatePendingEarlyAssignmentCancellations")]
    public void SchedulerCannotRevokeRunOrRestoreWakeOwnershipWhilePartyCleanupIsHeld(
        string pendingType, string dictionaryField, string runProperty, string updateMethod)
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var scheduler = new DadRuntime::dad.Services.DadSchedulerService(config, null!, null!, null!, null!,
            null!, null!, null!, DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var type = scheduler.GetType();
        var queried = new List<string>();
        var held = true;
        type.GetMethod("ConfigureScheduledRunCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [new Action<string>(_ => { }), new Func<string, bool>(run =>
            {
                queried.Add(run);
                return held && run == "exact-run";
            })]);
        var pending = Activator.CreateInstance(type.GetNestedType(pendingType, BindingFlags.NonPublic)!)!;
        pending.GetType().GetProperty(runProperty)!.SetValue(pending, "exact-run");
        var dictionary = (System.Collections.IDictionary)type.GetField(dictionaryField, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        dictionary.Add("exact-pending", pending);

        // All downstream/native services are deliberately absent: a held cleanup must
        // not even read a route, let alone dispatch a wake or run-context cancellation.
        type.GetMethod(updateMethod)!.Invoke(scheduler, null);
        Assert.Equal("exact-run", Assert.Single(queried));
        Assert.Single(dictionary.Keys.Cast<object>());
        Assert.Equal(DateTime.MinValue, Property<DateTime>(pending, "NextAttemptUtc"));

        held = false;
        pending.GetType().GetProperty("NextAttemptUtc")!.SetValue(pending, DateTime.MaxValue);
        pending.GetType().GetProperty("DeadlineLogged")!.SetValue(pending, true);
        type.GetMethod(updateMethod)!.Invoke(scheduler, null);
        Assert.Equal(2, queried.Count);
    }

    [Fact]
    public void DirectSchedulerCancelNotifiesPlannerBeforeCancellingPreparation()
    {
        var config = new DadRuntime::dad.Configuration { RunAsServerDad = true };
        var scheduler = new DadRuntime::dad.Services.DadSchedulerService(config, null!, null!, null!, null!,
            null!, null!, null!, DispatchProxy.Create<IPluginLog, DadScheduleDutyExitRetryRuntimeTests.QuietLog>());
        var state = new M.DadSchedulerPresetState
        {
            JobId = "job", PlannerRequestId = "exact-run", Phase = M.DadSchedulerPresetPhase.StartedPlanner,
        };
        var type = scheduler.GetType();
        type.GetField("currentState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scheduler, state);
        var calls = new List<string>();
        type.GetMethod("ConfigureScheduledRunCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [new Action<string>(run =>
            {
                Assert.Equal(M.DadSchedulerPresetPhase.StartedPlanner, state.Phase);
                calls.Add(run);
            }), new Func<string, bool>(_ => true)]);
        scheduler.Cancel("Cancel");
        Assert.Equal("exact-run", Assert.Single(calls));
        Assert.Equal(M.DadSchedulerPresetPhase.Cancelled, state.Phase);
    }

    private static object InstallPending(C coordinator)
    {
        var type = typeof(C).GetNestedType("ManagedCancellation", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(type, coordinator.CurrentResult.RequestId, "Cancelled by operator.")!;
        typeof(C).GetField("managedCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, pending);
        coordinator.CurrentResult.CancellationState = M.DadRunCancellationState.Cancelling;
        return pending;
    }

    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static object? Get(C coordinator, string name) => typeof(C).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);
    private static MethodInfo Method(string name) => typeof(C).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Invoke(C coordinator, string name, params object[] args) => Method(name).Invoke(coordinator, args);

    private static string Source(string path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(dir.FullName, "dad.csproj")) && File.Exists(file))
                return File.ReadAllText(file);
        }
        throw new InvalidOperationException("DAD source root was not resolved.");
    }
}
