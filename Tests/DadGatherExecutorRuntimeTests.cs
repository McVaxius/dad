extern alias DadRuntime;
using DadRuntime::dad.Models;
using DadRuntime::dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGatherExecutorRuntimeTests
{
    private static DadRunPlan Plan() => new()
    {
        CompositeModuleId = DadModuleId.GatherParty, RequiredParticipantCount = 3,
        Request = new()
        {
            RequestId = "gather-runtime",
            GatherParty = new() { Items = [new() { ItemId = 42, TargetQuantity = 1000 }] },
            GatherOrders = Enumerable.Range(1, 3).Select(index => new DadGatherWorkerOrder(
                $"Slot{index}", $"worker-{index}", $"character-{index}", 42, 0, index == 1 ? 334 : 333,
                index == 1 ? 334 : 333, 0, $"account-{index}")).ToList(),
        },
        Modules = [new() { ModuleId = DadModuleId.GatherParty, DisplayName = "GBR Gather Party", ExpectedPartySize = 3 }],
    };
    private static List<DadParticipantSnapshot> View(int local) => Enumerable.Range(1, 3).Select(index => new DadParticipantSnapshot
    {
        AssignedSlotId = $"Slot{index}", WorkerSessionId = new($"worker-{index}"),
        ActiveCharacterKey = new($"character-{index}"), ManagedAccountKey = new($"account-{index}"), IsLocalClient = local == index,
    }).ToList();

    [Fact]
    public void ProductionExecutorRunsEveryWorkerAndProvesEachFiniteShare()
    {
        var plan = Plan();
        for (var index = 1; index <= 3; index++)
        {
            var native = new Native();
            var executor = new DadGatherPartyExecutor(native);
            var started = executor.Start(plan, View(index));
            Assert.True(started.Success, started.Summary);
            Assert.True(started.ExecutorStatus.IsActive);
            Assert.Equal(index == 1 ? 334 : 333, Assert.Single(native.Orders).Quantity);
            Assert.Equal($"worker-{index}", native.Orders[0].WorkerSessionId);
            executor.Start(plan, View(index));
            Assert.Equal(1, native.Starts);
            native.Count = index == 1 ? 335 : 333;
            var finished = executor.Update();
            Assert.True(finished.Success, finished.Summary);
            Assert.Equal(DadRunStatus.Completed, finished.ExecutorStatus.Status);
            Assert.Equal(native.Count, finished.ExecutorStatus.GatherCounts[42]);
            Assert.False(finished.ExecutorStatus.IsActive);
            Assert.False(executor.HasOwnedWork);
            Assert.Equal(1, native.Stops);
        }
    }

    [Fact]
    public void WaitingParticipantCanBeCancelledWithoutClaimingCountCompletion()
    {
        var native = new Native { Waiting = true };
        var executor = new DadGatherPartyExecutor(native);
        executor.Start(Plan(), View(2));
        var waiting = executor.Update();
        Assert.True(waiting.ExecutorStatus.IsActive);
        Assert.Equal(DadRunStatus.Running, waiting.ExecutorStatus.Status);
        Assert.Equal(0, waiting.ExecutorStatus.GatherCounts[42]);
        var stopped = executor.Cancel("Stop");
        Assert.False(stopped.Success);
        Assert.Equal(DadRunStatus.Cancelled, stopped.ExecutorStatus.Status);
        Assert.False(executor.HasOwnedWork);
        Assert.Equal(1, native.Starts);
        Assert.Equal(1, native.Stops);
    }

    [Fact]
    public void FailedCleanupStaysVisibleAndPreventsAnotherNativeStart()
    {
        var native = new Native { StopFails = true };
        var executor = new DadGatherPartyExecutor(native);
        executor.Start(Plan(), View(2));
        var failure = executor.Cancel("Stop");
        Assert.Equal(DadRunStatus.Failed, failure.ExecutorStatus.Status);
        Assert.True(executor.HasOwnedWork);
        Assert.Contains("unresolved", failure.Summary);
        var different = Plan();
        different.Request.RequestId = "another-run";
        Assert.False(executor.Start(different, View(2)).Success);
        Assert.Equal(1, native.Starts);
        native.StopFails = false;
        var cancelled = executor.Cancel("Stop again");
        Assert.Equal(DadRunStatus.Cancelled, cancelled.ExecutorStatus.Status);
        Assert.Equal("gather-runtime", cancelled.RunId);
        Assert.False(executor.HasOwnedWork);
    }

    private sealed class Native : IDadGatherClientService, IDadGatherClientSession
    {
        public bool HasOwnership { get; private set; }
        public bool Waiting, StopFails;
        public int Starts, Stops;
        public long Count;
        public DadGatherWorkerOrder[] Orders = [];
        public DadGatherStockReply Probe(DadGatherStockQuery query) => throw new InvalidOperationException("Executor cannot invent a stock probe.");
        public bool TryCreateSession(out IDadGatherClientSession? session, out string error) { session = this; error = ""; return true; }
        public bool TryReadCarriedCount(uint itemId, out long count, out string error) { count = Count; error = ""; return true; }
        public bool TryReadActivity(string runId, out bool enabled, out bool waiting, out string error)
        { enabled = HasOwnership; waiting = Waiting; error = ""; return true; }
        public bool TryStart(string runId, IReadOnlyList<DadGatherWorkerOrder> orders, out string error)
        { Starts++; Orders = orders.ToArray(); HasOwnership = true; error = ""; return true; }
        public bool TryStop(string runId, out string error)
        {
            Stops++; error = StopFails ? "Native stop failed" : "";
            if (StopFails) return false;
            HasOwnership = false; return true;
        }
    }
}
