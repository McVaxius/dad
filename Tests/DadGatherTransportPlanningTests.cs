using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGatherTransportPlanningTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc);
    private static DadParticipantSnapshot Participant(int number) => new()
    {
        AssignedSlotId = $"Slot{number}", WorkerSessionId = new($"worker-{number}"),
        ManagedAccountKey = new($"account-{number}"), ActiveCharacterKey = new($"character-{number}"),
        IsLocalClient = number == 1,
    };
    private static DadGatherPartyTask Task() => new()
    { Items = [new() { ItemId = 42, TargetQuantity = 1000 }] };
    private static DadGatherStockReply Reply(DadGatherStockQuery query, long count = 0) => new()
    {
        RequestId = query.RequestId, RunId = query.RunId, SlotId = query.SlotId,
        WorkerSessionId = query.WorkerSessionId, AccountKey = query.AccountKey, CharacterKey = query.CharacterKey,
        ObservedAtUtc = Now, Available = true, CarriedCounts = new() { [42] = count }, EligibleItems = [42],
    };

    [Fact]
    public void QueriesOnlySelectedMaterialsOncePerResponseAndFreezesExactAllocations()
    {
        var participants = Enumerable.Range(1, 3).Select(Participant).ToArray();
        var probe = new DadGatherPlanProbe(Task(), participants, "run", Now);
        var calls = new List<DadGatherStockQuery>();
        probe.Update((_, query) =>
        {
            calls.Add(query);
            Assert.Equal(new uint[] { 42 }, query.ItemIds);
            return Reply(query, query.SlotId == "Slot1" ? 100 : query.SlotId == "Slot2" ? 50 : 0);
        }, Now, TimeSpan.FromMinutes(1));
        Assert.False(probe.Pending);
        Assert.True(probe.Preview!.CanStart);
        var row = Assert.Single(probe.Preview.Items);
        Assert.Equal(new[] { 284, 283, 283 }, row.Orders.Select(order => order.Quantity));
        Assert.Equal(new long[] { 384, 333, 283 }, row.Orders.Select(order => order.NativeTarget));
        var request = new DadRunRequest { GatherParty = Task(), GatherOrders = row.Orders.ToList() };
        Assert.True(DadGatherOrderRules.TryValidate(request, participants, out var error), error);
        probe.Update((_, _) => throw new InvalidOperationException("finished queries must not replay"), Now.AddSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal(3, calls.Count);
        Assert.Equal(3, calls.Select(query => query.RequestId).Distinct().Count());
    }

    [Theory]
    [InlineData("request")]
    [InlineData("run")]
    [InlineData("slot")]
    [InlineData("worker")]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("schema")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("negative")]
    [InlineData("unsupported")]
    [InlineData("unavailable")]
    public void ForeignOrIncompleteStockNeverBecomesInventoryTruth(string problem)
    {
        var probe = new DadGatherPlanProbe(Task(), [Participant(1)], "run", Now);
        probe.Update((_, query) =>
        {
            var reply = Reply(query);
            switch (problem)
            {
                case "request": reply.RequestId = "other"; break;
                case "run": reply.RunId = "other"; break;
                case "slot": reply.SlotId = "other"; break;
                case "worker": reply.WorkerSessionId = new("other"); break;
                case "account": reply.AccountKey = new("other"); break;
                case "character": reply.CharacterKey = new("other"); break;
                case "schema": reply.SchemaVersion = 3; break;
                case "stale": reply.ObservedAtUtc = Now.AddMinutes(-5); break;
                case "future": reply.ObservedAtUtc = Now.AddMinutes(5); break;
                case "missing": reply.CarriedCounts.Clear(); break;
                case "extra": reply.CarriedCounts[43] = 0; break;
                case "negative": reply.CarriedCounts[42] = -1; break;
                case "unsupported": reply.EligibleItems.Add(99); break;
                case "unavailable": reply.Available = false; break;
            }
            return reply;
        }, Now, TimeSpan.FromMinutes(1));
        Assert.False(probe.Pending);
        Assert.Null(probe.Preview);
        Assert.NotEmpty(probe.Error);
        probe.Update((_, _) => throw new InvalidOperationException("failed query must not replay"), Now, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void MissingResponseWaitsThenStopsWithoutReplacingRequestIdentity()
    {
        var probe = new DadGatherPlanProbe(Task(), [Participant(1)], "run", Now);
        var ids = new List<string>();
        for (var second = 0; second < 60; second += 10)
            probe.Update((_, query) => { ids.Add(query.RequestId); return null; }, Now.AddSeconds(second), TimeSpan.FromMinutes(1));
        Assert.True(probe.Pending);
        Assert.Single(ids.Distinct());
        probe.Update((_, _) => throw new InvalidOperationException("timeout must not dispatch"), Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.False(probe.Pending);
        Assert.Contains("timed out", probe.Error);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("worker")]
    [InlineData("slot")]
    [InlineData("item")]
    [InlineData("quantity")]
    [InlineData("threshold")]
    [InlineData("location")]
    [InlineData("duplicate")]
    public void WorkerOrdersCannotChangeFrozenIdentityOrDeficit(string problem)
    {
        var participant = Participant(1);
        var order = new DadGatherWorkerOrder("Slot1", "worker-1", "character-1", 42, 0, 1000, 1000, 0, "account-1");
        var request = new DadRunRequest { GatherParty = Task(), GatherOrders = [order] };
        Assert.True(DadGatherOrderRules.TryValidate(request, [participant], out _));
        request.GatherOrders[0] = problem switch
        {
            "account" => order with { AccountKey = "other" },
            "worker" => order with { WorkerSessionId = "other" },
            "slot" => order with { SlotId = "other" },
            "item" => order with { ItemId = 43 },
            "quantity" => order with { Quantity = 1, NativeTarget = 1 },
            "threshold" => order with { NativeTarget = 1 },
            "location" => order with { PreferredLocationId = 7 },
            _ => order,
        };
        if (problem == "duplicate") request.GatherOrders.Add(order);
        Assert.False(DadGatherOrderRules.TryValidate(request, [participant], out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void StockWireRoundtripKeepsConcreteInventoryAndEligibility()
    {
        var query = new DadGatherStockQuery { RunId = "run", SlotId = "Slot1", WorkerSessionId = new("worker-1"), AccountKey = new("account-1"), CharacterKey = new("character-1"), RequestedAtUtc = Now, ItemIds = [42] };
        var reply = DadIpcJson.Deserialize<DadGatherStockReply>(DadIpcJson.Serialize(Reply(query, 12)))!;
        Assert.True(DadGatherStockQueryRules.TryRead(query, reply, Now, out var stock, out var error), error);
        Assert.Equal(12, stock!.CarriedCounts[42]);
        Assert.Contains(42u, stock.EligibleItems);
    }
}
