using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGatherPartyPlanningTests
{
    [Theory]
    [InlineData(0, 0, 0, 334, 333, 333)]
    [InlineData(100, 50, 0, 284, 283, 283)]
    public void SplitsOnlyTheCombinedCarriedDeficitAndConvertsToNativeThresholds(
        int first, int second, int third, int share1, int share2, int share3)
    {
        var task = Task(1000);
        var workers = new[] { Worker(1, first), Worker(2, second), Worker(3, third) };
        var preview = DadGatherPartyPlanningRules.Preview(task, workers);
        Assert.True(preview.CanStart, string.Join("; ", preview.Blockers));
        var item = Assert.Single(preview.Items);
        Assert.Equal(first + second + third, item.OnHand);
        Assert.Equal(new[] { share1, share2, share3 }, item.Orders.Select(order => order.Quantity));
        Assert.Equal(new long[] { first + share1, second + share2, third + share3 }, item.Orders.Select(order => order.NativeTarget));
        Assert.Equal(item.Remaining, item.Orders.Sum(order => (long)order.Quantity));
        Assert.All(item.Orders, order => Assert.Equal(1000, item.Target));
        Assert.Equal(1000, task.Items[0].TargetQuantity);
        Assert.Equal(first, workers[0].CarriedCounts[42]);
    }

    [Fact]
    public void AlreadyMetTargetNeedsNeitherEligibilityNorPositiveOrders()
    {
        var preview = DadGatherPartyPlanningRules.Preview(Task(1000),
            [Worker(1, 1001) with { EligibleItems = new HashSet<uint>() }, Worker(2, 0)]);
        Assert.True(preview.Fulfilled);
        Assert.All(Assert.Single(preview.Items).Orders, order => Assert.Equal(0, order.Quantity));
    }

    [Fact]
    public void FewerItemsThanClientsLeavesZeroSharesInsteadOfNativeClampingToOne()
    {
        var preview = DadGatherPartyPlanningRules.Preview(Task(1), [Worker(1), Worker(2), Worker(3)]);
        Assert.True(preview.CanStart);
        Assert.Equal(new[] { 1, 0, 0 }, Assert.Single(preview.Items).Orders.Select(order => order.Quantity));
    }

    [Fact]
    public void CountsAllSelectedStockButAssignsOnlyEligibleClients()
    {
        var preview = DadGatherPartyPlanningRules.Preview(Task(100),
            [Worker(1, 50) with { EligibleItems = new HashSet<uint>() }, Worker(2, 10), Worker(3)]);
        Assert.True(preview.CanStart);
        Assert.Equal(new[] { 0, 20, 20 }, Assert.Single(preview.Items).Orders.Select(order => order.Quantity));
    }

    [Fact]
    public void ManualSpecializationIsFixedAndRevalidatedAgainstCurrentStock()
    {
        var task = Task(100);
        task.Items[0].PreferredLocationId = 77;
        task.Allocations = [new() { SlotId = "slot-2", ItemId = 42, Quantity = 100 }];
        var first = DadGatherPartyPlanningRules.Preview(task, [Worker(1), Worker(2)]);
        Assert.True(first.CanStart);
        Assert.Equal(new[] { 0, 100 }, Assert.Single(first.Items).Orders.Select(order => order.Quantity));
        Assert.All(first.Items[0].Orders, order => Assert.Equal(77u, order.PreferredLocationId));
        var changed = DadGatherPartyPlanningRules.Preview(task, [Worker(1, 10), Worker(2)]);
        Assert.False(changed.CanStart);
        Assert.Contains(changed.Blockers, blocker => blocker.Contains("current deficit 90"));
        Assert.Equal(100, Assert.Single(task.Allocations).Quantity);
    }

    [Theory]
    [InlineData("stock")]
    [InlineData("ready")]
    [InlineData("account")]
    [InlineData("session")]
    [InlineData("character")]
    [InlineData("slot")]
    [InlineData("negative")]
    public void UnknownStockOrDuplicatedClientIdentityNeverBecomesSuccess(string failure)
    {
        var first = Worker(1);
        var second = Worker(2);
        second = failure switch
        {
            "stock" => second with { InventoryAvailable = false },
            "ready" => second with { Ready = false },
            "account" => second with { AccountKey = first.AccountKey },
            "session" => second with { WorkerSessionId = first.WorkerSessionId },
            "character" => second with { CharacterKey = first.CharacterKey },
            "slot" => second with { SlotId = first.SlotId },
            _ => second with { CarriedCounts = new Dictionary<uint, long> { [42] = -1 } },
        };
        var preview = DadGatherPartyPlanningRules.Preview(Task(100), [first, second]);
        Assert.False(preview.CanStart);
        Assert.False(preview.Fulfilled);
        Assert.NotEmpty(preview.Blockers);
    }

    [Fact]
    public void MissingEligibilityIsVisibleAndDisabledInvalidRowsStayExcluded()
    {
        var task = Task(100);
        var unavailable = DadGatherPartyPlanningRules.Preview(task,
            [Worker(1) with { EligibleItems = new HashSet<uint>() }]);
        Assert.False(unavailable.CanStart);
        task.Items.Add(new() { ItemId = 0, TargetQuantity = 0, Enabled = false });
        var enabledOnly = DadGatherPartyPlanningRules.Preview(task, [Worker(1)]);
        Assert.True(enabledOnly.CanStart);
        Assert.Single(enabledOnly.Items);
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("ineligible")]
    public void InvalidManualAllocationsDoNotSilentlyRebalance(string failure)
    {
        var task = Task(100);
        task.Allocations = [new() { SlotId = failure == "unknown" ? "missing" : "slot-1", ItemId = 42,
            Quantity = failure == "sum" ? 99 : failure == "negative" ? -1 : 100 }];
        if (failure == "duplicate") task.Allocations.Add(task.Allocations[0].Clone());
        var worker = Worker(1);
        if (failure == "ineligible") worker = worker with { EligibleItems = new HashSet<uint>() };
        var preview = DadGatherPartyPlanningRules.Preview(task, [worker, Worker(2)]);
        Assert.False(preview.CanStart);
        Assert.NotEmpty(preview.Blockers);
    }

    [Fact]
    public void SavedTaskAndClonesPreserveRowIdentityAndIsolateEdits()
    {
        var task = Task(1000);
        task.Allocations.Add(new() { SlotId = "slot-1", ItemId = 42, Quantity = 1000 });
        var restored = DadIpcJson.Deserialize<DadGatherPartyTask>(DadIpcJson.Serialize(task));
        Assert.NotNull(restored);
        Assert.Equal(task.Items[0].RowId, restored.Items[0].RowId);
        var clone = restored.Clone();
        clone.Items[0].TargetQuantity = 10;
        clone.Allocations[0].Quantity = 10;
        Assert.Equal(1000, task.Items[0].TargetQuantity);
        Assert.Equal(1000, restored.Allocations[0].Quantity);
    }

    private static DadGatherPartyTask Task(int target) => new()
    {
        Items = [new() { ItemId = 42, TargetQuantity = target }],
    };

    private static DadGatherWorkerStock Worker(int id, long count = 0) => new(
        $"slot-{id}", $"worker-{id}", $"account-{id}", $"character-{id}", true, true,
        new Dictionary<uint, long> { [42] = count }, new HashSet<uint> { 42 });
}
