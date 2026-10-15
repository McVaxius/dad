using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGatherWorkerRunTests
{
    private static DadGatherWorkerOrder Order(uint item = 42, int quantity = 5)
        => new("slot-1", "session-1", "character-1", item, 10, quantity, 10 + quantity, 0);

    [Fact]
    public void TimedNodeWaitingNeverExpiresAndPartialStockIsNotCompletion()
    {
        var host = new DadGatherBuddyListAdapterTests.NativeHost();
        var original = host.ExistingList(true);
        var run = new DadGatherWorkerRun(host.Adapter());
        var now = DateTime.UtcNow;
        run.Start("run", [Order(), Order(43, 7)], now);
        host.Waiting = true;
        run.Update(now.AddDays(1));
        Assert.Equal(DadGatherWorkerRunState.Waiting, run.State);
        Assert.True(run.IsActive);
        host.GameData.Gatherables[42].CarriedCount = 17; // Whole-node overshoot is valid.
        run.Update(now.AddDays(1).AddSeconds(1));
        Assert.Equal(DadGatherWorkerRunState.Waiting, run.State);
        host.GameData.Gatherables[43].CarriedCount = 17;
        run.Update(now.AddDays(1).AddSeconds(2));
        Assert.Equal(DadGatherWorkerRunState.Completed, run.State);
        Assert.False(run.HasOwnership);
        Assert.True(original.Enabled);
        Assert.Equal(new[] { true, false }, host.EnabledCalls);
        run.Start("run", [Order(), Order(43, 7)], now);
        run.Update(now.AddDays(5));
        Assert.Equal(2, host.EnabledCalls.Count);
        Assert.Equal(17, run.Counts[43]);
    }

    [Fact]
    public void DisabledGbrAndRecordedRetainerStockDoNotProveCompletion()
    {
        var host = new DadGatherBuddyListAdapterTests.NativeHost();
        var run = new DadGatherWorkerRun(host.Adapter());
        var now = DateTime.UtcNow;
        run.Start("run", [Order()], now);
        host.Enabled = false;
        run.Update(now);
        Assert.Equal(DadGatherWorkerRunState.Failed, run.State);
        Assert.Equal(10, run.Counts[42]);
        Assert.Contains("before", run.Summary);
        Assert.False(run.HasOwnership);
    }

    [Fact]
    public void StopIsOwnedAndDoesNotTriggerAnotherStartOrComplete()
    {
        var host = new DadGatherBuddyListAdapterTests.NativeHost();
        var run = new DadGatherWorkerRun(host.Adapter());
        run.Cancel("pending");
        Assert.Empty(host.EnabledCalls);
        run.Start("run", [Order()], DateTime.UtcNow);
        run.Cancel("Stop");
        Assert.Equal(DadGatherWorkerRunState.Cancelled, run.State);
        run.Start("run", [Order()], DateTime.UtcNow);
        run.Cancel("again");
        Assert.Equal(new[] { true, false }, host.EnabledCalls);
        Assert.Empty(host.AutoGatherListsManager.Lists);
    }

    [Fact]
    public void ChangedNativeListOrInstanceFailsInsteadOfInferringSuccess()
    {
        var host = new DadGatherBuddyListAdapterTests.NativeHost();
        var run = new DadGatherWorkerRun(host.Adapter());
        run.Start("run", [Order()], DateTime.UtcNow);
        host.GameData.Gatherables[42].CarriedCount = 99;
        host.Current = false;
        run.Update(DateTime.UtcNow);
        Assert.Equal(DadGatherWorkerRunState.Failed, run.State);
        Assert.True(run.HasOwnership);
        Assert.Contains("unresolved", run.Summary);
        Assert.Equal(new[] { true }, host.EnabledCalls);
    }

    [Fact]
    public void ZeroShareDoesNotStartNativeWork()
    {
        var host = new DadGatherBuddyListAdapterTests.NativeHost();
        var run = new DadGatherWorkerRun(host.Adapter());
        run.Start("run", [Order(quantity: 0)], DateTime.UtcNow);
        Assert.Equal(DadGatherWorkerRunState.Completed, run.State);
        Assert.Empty(host.AutoGatherListsManager.Lists);
        Assert.Empty(host.EnabledCalls);
    }

    [Fact]
    public void ThreeFixedWorkersFinishTheirOwnSharesWithoutRedistribution()
    {
        var task = new DadGatherPartyTask { Items = [new() { ItemId = 42, TargetQuantity = 1000 }] };
        var hosts = Enumerable.Range(1, 3).Select(_ => new DadGatherBuddyListAdapterTests.NativeHost()).ToArray();
        foreach (var host in hosts) host.GameData.Gatherables[42].CarriedCount = 0;
        var stocks = Enumerable.Range(1, 3).Select(index => new DadGatherWorkerStock(
            $"slot-{index}", $"session-{index}", $"account-{index}", $"character-{index}", true, true,
            new Dictionary<uint, long> { [42] = 0 }, new HashSet<uint> { 42 })).ToArray();
        var plan = DadGatherPartyPlanningRules.Preview(task, stocks);
        Assert.True(plan.CanStart);
        var runs = hosts.Select(host => new DadGatherWorkerRun(host.Adapter())).ToArray();
        var now = DateTime.UtcNow;
        for (var index = 0; index < 3; index++)
            runs[index].Start("group-run", [plan.Items[0].Orders[index]], now);
        hosts[0].GameData.Gatherables[42].CarriedCount = 334;
        runs[0].Update(now);
        Assert.Equal(DadGatherWorkerRunState.Completed, runs[0].State);
        Assert.All(runs.Skip(1), run => Assert.True(run.IsActive));
        runs[1].Cancel("worker lost");
        hosts[2].GameData.Gatherables[42].CarriedCount = 333;
        runs[2].Update(now);
        Assert.Equal(DadGatherWorkerRunState.Cancelled, runs[1].State);
        Assert.Equal(DadGatherWorkerRunState.Completed, runs[2].State);
        Assert.Equal(new[] { 334, 333, 333 }, plan.Items[0].Orders.Select(order => order.Quantity));
        Assert.All(hosts, host => Assert.Equal(new[] { true, false }, host.EnabledCalls));
    }
}
