using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadGatherPersistenceTests
{
    [Fact]
    public void ActiveGatheringShareRoundtripPreservesTargetsNodesAndFixedSlotAmounts()
    {
        var group = Group();
        group.GroupId = "11111111111111111111111111111111";
        group.RunFamily = DadPlannerRunFamily.FarmLoops;
        var service = new DadShareService();
        Assert.True(service.TryExportPlan(group, [], out var encoded, out var error), error);
        Assert.True(service.TryDecode(encoded, DadShareConstants.PlanKind, out var envelope, out error), error);
        var task = envelope!.Plan!.GatherParty!;
        Assert.Equal(group.GatherParty.Items[0].RowId, task.Items[0].RowId);
        Assert.Equal(7u, task.Items[0].PreferredLocationId);
        Assert.Equal(1000, task.Items[0].TargetQuantity);
        Assert.Equal(334, Assert.Single(task.Allocations).Quantity);
        var imported = service.Apply(envelope, [], [], commandValuesConfirmed: true);
        Assert.True(imported.Success, imported.Summary);
        var restored = Assert.Single(imported.PlannerGroups);
        Assert.Equal(1000, restored.GatherParty.Items[0].TargetQuantity);
        restored.GatherParty.Items[0].TargetQuantity = 50;
        Assert.Equal(1000, group.GatherParty.Items[0].TargetQuantity);
    }

    [Fact]
    public void OrdinaryShareDtoOmitsTheNewOptionalPayloadForOlderReaders()
    {
        var encoded = DadIpcJson.Serialize(new DadSharePlanDto { ActivityMode = DadPlannerActivityMode.DutySupport });
        Assert.DoesNotContain("gatherParty", encoded, StringComparison.OrdinalIgnoreCase);
    }

    private static DadPlannerGroup Group() => new()
    {
        GroupId = "existing-group", ActivityMode = DadPlannerActivityMode.GatherParty,
        GatherParty = new()
        {
            Name = "Iron", Items = [new() { ItemId = 42, Name = "Iron Ore", TargetQuantity = 1000, PreferredLocationId = 7 }],
            Allocations = [new() { SlotId = "Slot1", ItemId = 42, Quantity = 334 }],
        },
        Slots = [new() { SlotId = "Slot1" }],
    };

    [Fact]
    public void PresetRoundtripRenameAndSchedulerCloneKeepStableRowsAndIndependentTargets()
    {
        var group = Group();
        var restored = DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group))!;
        Assert.Equal(group.GroupId, restored.GroupId);
        Assert.Equal(group.GatherParty.Items[0].RowId, restored.GatherParty.Items[0].RowId);
        Assert.Equal(7u, restored.GatherParty.Items[0].PreferredLocationId);
        Assert.Equal(334, Assert.Single(restored.GatherParty.Allocations).Quantity);
        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(restored, restored.Slots);
        clone.DisplayName = "Renamed";
        clone.GatherParty.Items[0].TargetQuantity = 50;
        clone.GatherParty.Allocations[0].Quantity = 50;
        Assert.Equal(1000, restored.GatherParty.Items[0].TargetQuantity);
        Assert.Equal(334, restored.GatherParty.Allocations[0].Quantity);
        DadPlannerGroupUpdateRules.ApplyPlannerFields(restored, clone, DateTime.UtcNow);
        Assert.Equal("Renamed", restored.DisplayName);
        Assert.Equal(50, restored.GatherParty.Items[0].TargetQuantity);
        clone.GatherParty.Items.Clear();
        Assert.Single(restored.GatherParty.Items);
    }

    [Fact]
    public void OldPresetDefaultsStayEmptyAndEnumValuesAreAppendOnly()
    {
        var old = DadIpcJson.Deserialize<DadPlannerGroup>("{\"ActivityMode\":16,\"GroupId\":\"old\"}")!;
        Assert.Equal(DadPlannerActivityMode.LootGoblin, old.ActivityMode);
        Assert.Empty(old.GatherParty.Items);
        Assert.Equal(15, (int)DadModuleId.LootGoblin);
        Assert.Equal(16, (int)DadModuleId.GatherParty);
        Assert.Equal(17, (int)DadPlannerActivityMode.GatherParty);
    }

    [Fact]
    public void RunTaskCloneAndWireSchemaCannotLoseGatheringOrders()
    {
        var request = new DadRunRequest { GatherParty = Group().GatherParty };
        Assert.Equal(1, request.GetConfiguredTaskCount());
        Assert.Equal(DadModuleId.GatherParty, request.ApplyOrchestrationDefaults().ModuleTarget);
        Assert.Contains("Iron", request.DescribeRequestedWork());
        var clone = request.Clone();
        clone.GatherParty!.Items.Clear();
        Assert.Single(request.GatherParty.Items);
        Assert.Equal(4, DadWorkerCommandSchemaRules.ResolveEmissionSchema(null, null, request.GatherParty));
        Assert.Equal(1, DadWorkerCommandSchemaRules.ResolveEmissionSchema(null));
        Assert.Equal(2, DadWorkerCommandSchemaRules.ResolveEmissionSchema(new() { Enabled = true }));
        var command = new DadWorkerExecutionCommand { Plan = new() { Request = request } };
        Assert.False(DadWorkerCommandValidationRules.TryValidate(command, new(), out _, out var error));
        Assert.Contains("schema 4", error);
    }
}
