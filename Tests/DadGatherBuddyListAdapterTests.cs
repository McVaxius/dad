using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests
{
    public sealed class DadGatherBuddyListAdapterTests
    {
        private static DadGatherWorkerOrder Order(uint item = 42, int quantity = 5, long stock = 10, uint location = 0)
            => new("slot-1", "session-1", "character-1", item, stock, quantity, stock + quantity, location);

        [Fact]
        public void OwnsOneFiniteCarriedOnlyListAndRestoresExistingListFlags()
        {
            var host = new NativeHost();
            var original = host.ExistingList(true);
            var disabled = host.ExistingList(false);
            var adapter = host.Adapter();
            Assert.True(adapter.TryReadCarriedCount(42, out var count, out var error), error);
            Assert.Equal(10, count); // Retainer-inclusive native count is deliberately 9999.
            Assert.True(adapter.TryStart("run", [Order(location: 7)], out error), error);
            var owned = Assert.Single(host.AutoGatherListsManager.Lists, list => list != original && list != disabled);
            Assert.False(original.Enabled);
            Assert.False(disabled.Enabled);
            Assert.False(owned.UsesRetainerInventory);
            Assert.False(owned.Fallback);
            Assert.False(owned.RemoveCompletedItems);
            Assert.Equal(15u, Assert.Single(owned.Quantities).Value);
            Assert.Equal(7u, Assert.Single(owned.PreferredLocations).Value.Id);
            Assert.Equal(new[] { true }, host.EnabledCalls);
            Assert.True(adapter.TryStart("run", [Order(location: 7)], out error), error);
            Assert.Single(host.EnabledCalls);
            Assert.True(adapter.TryStop("run", out error), error);
            Assert.False(adapter.HasOwnership);
            Assert.True(original.Enabled);
            Assert.False(disabled.Enabled);
            Assert.DoesNotContain(owned, host.AutoGatherListsManager.Lists);
            Assert.Equal(new[] { true, false }, host.EnabledCalls);
            Assert.False(adapter.TryStart("run", [Order()], out _));
        }

        [Fact]
        public void UnownedAndMismatchedStopsNeverDisableSomeoneElsesGathering()
        {
            var host = new NativeHost();
            var adapter = host.Adapter();
            host.Enabled = true;
            Assert.True(adapter.TryStop("anything", out _));
            Assert.Empty(host.EnabledCalls);
            Assert.False(adapter.TryStart("run", [Order()], out _));
            Assert.Empty(host.AutoGatherListsManager.Lists);
            host.Enabled = false;
            Assert.True(adapter.TryStart("run", [Order()], out _));
            Assert.False(adapter.TryStop("foreign", out _));
            Assert.True(host.Enabled);
            Assert.True(adapter.HasOwnership);
        }

        [Theory]
        [InlineData("stock")]
        [InlineData("item")]
        [InlineData("location")]
        [InlineData("negative")]
        [InlineData("mixed")]
        [InlineData("threshold")]
        [InlineData("duplicate")]
        public void InvalidOrStaleOrdersFailBeforeNativeMutation(string problem)
        {
            var host = new NativeHost();
            var original = host.ExistingList(true);
            var adapter = host.Adapter();
            var order = problem switch
            {
                "stock" => Order(stock: 11),
                "item" => Order(item: 999),
                "location" => Order(location: 999),
                "negative" => Order(quantity: -1),
                "threshold" => Order() with { NativeTarget = 1 },
                _ => Order(),
            };
            DadGatherWorkerOrder[] orders = problem == "mixed"
                ? [order, Order(item: 43) with { SlotId = "other" }]
                : problem == "duplicate" ? [order, order] : [order];
            Assert.False(adapter.TryStart("run", orders, out var error));
            Assert.NotEmpty(error);
            Assert.True(original.Enabled);
            Assert.Same(original, Assert.Single(host.AutoGatherListsManager.Lists));
            Assert.Empty(host.EnabledCalls);
            Assert.False(adapter.HasOwnership);
        }

        [Fact]
        public void ZeroSharesNeverCreateNativeRowsOrEnableGbr()
        {
            var host = new NativeHost();
            Assert.True(host.Adapter().TryStart("run", [Order(quantity: 0)], out _));
            Assert.Empty(host.EnabledCalls);
            Assert.Empty(host.AutoGatherListsManager.Lists);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UncertainStartIsCleanedUpAndNeverReplayed(bool enabledBeforeException)
        {
            var host = new NativeHost { ThrowOnEnable = true, EnableBeforeThrow = enabledBeforeException };
            var original = host.ExistingList(true);
            var adapter = host.Adapter();
            Assert.False(adapter.TryStart("run", [Order()], out var error));
            Assert.Contains("enable failed", error);
            Assert.True(original.Enabled);
            Assert.False(host.Enabled);
            Assert.False(adapter.HasOwnership);
            var actions = host.EnabledCalls.Count;
            Assert.False(adapter.TryStart("run", [Order()], out _));
            Assert.Equal(actions, host.EnabledCalls.Count);
        }

        [Fact]
        public void WrongActiveThresholdNeverStartsAndRestoresSuppressedLists()
        {
            var host = new NativeHost();
            var original = host.ExistingList(true);
            host.AutoGatherListsManager.CorruptActive = true;
            Assert.False(host.Adapter().TryStart("run", [Order()], out var error));
            Assert.Contains("active items", error);
            Assert.True(original.Enabled);
            Assert.Empty(host.EnabledCalls);
            Assert.Same(original, Assert.Single(host.AutoGatherListsManager.Lists));
        }

        [Fact]
        public void PluginReplacementIsNeverMutatedByOldOwner()
        {
            var host = new NativeHost();
            var adapter = host.Adapter();
            Assert.True(adapter.TryStart("run", [Order()], out _));
            host.Current = false;
            Assert.False(adapter.TryReadCarriedCount(42, out _, out _));
            Assert.False(adapter.TryStart("run", [Order()], out _));
            Assert.False(adapter.TryStop("run", out _));
            Assert.True(adapter.HasOwnership);
            Assert.Equal(new[] { true }, host.EnabledCalls);
        }

        [Fact]
        public void UserReplacementOfActiveListsIsNotGloballyStopped()
        {
            var host = new NativeHost();
            var original = host.ExistingList(true);
            var adapter = host.Adapter();
            Assert.True(adapter.TryStart("run", [Order()], out _));
            var owned = host.AutoGatherListsManager.Lists.Last();
            owned.Enabled = false;
            var user = host.ExistingList(true);
            user.Add(host.GameData.Gatherables[43], 30);
            host.AutoGatherListsManager.SetActiveItems(false);
            Assert.True(adapter.TryStop("run", out var error), error);
            Assert.True(user.Enabled);
            Assert.True(original.Enabled);
            Assert.True(host.Enabled);
            Assert.Equal(new[] { true }, host.EnabledCalls);
            Assert.DoesNotContain(owned, host.AutoGatherListsManager.Lists);
        }

        [Fact]
        public void CleanupFailureRetainsVisibleOwnership()
        {
            var host = new NativeHost();
            var adapter = host.Adapter();
            Assert.True(adapter.TryStart("run", [Order()], out _));
            host.AutoGatherListsManager.ThrowOnDelete = true;
            Assert.False(adapter.TryStop("run", out var error));
            Assert.Contains("delete failed", error);
            Assert.True(adapter.HasOwnership);
            Assert.False(adapter.TryStart("other", [Order()], out _));
        }

        [Fact]
        public void StopFailureKeepsTheOwnedListAndAnExplicitLaterStopCanCleanIt()
        {
            var host = new NativeHost();
            var original = host.ExistingList(true);
            var adapter = host.Adapter();
            Assert.True(adapter.TryStart("run", [Order()], out _));
            host.ThrowOnDisable = true;
            Assert.False(adapter.TryStop("run", out _));
            Assert.True(adapter.HasOwnership);
            Assert.False(original.Enabled);
            Assert.True(host.Enabled);
            host.ThrowOnDisable = false;
            Assert.True(adapter.TryStop("run", out var error), error);
            Assert.False(host.Enabled);
            Assert.True(original.Enabled);
            Assert.False(adapter.HasOwnership);
        }

        [Fact]
        public void UnreadableInventoryBlocksBeforeAnyListOrToggleChanges()
        {
            var host = new NativeHost();
            var original = host.ExistingList(true);
            var adapter = new DadGatherBuddyListAdapter(host, () => true, () => false, _ => throw new Exception("No toggle expected"), inventoryReady: () => false);
            Assert.False(adapter.TryReadCarriedCount(42, out _, out var error));
            Assert.Contains("inventory", error);
            Assert.False(adapter.TryStart("run", [Order()], out _));
            Assert.True(original.Enabled);
            Assert.Same(original, Assert.Single(host.AutoGatherListsManager.Lists));
        }

        [Fact]
        public void NativeRequirementsKeepMinerAndBotanistDistinctAndRejectUnsupportedTypes()
        {
            var host = new NativeHost();
            var adapter = host.Adapter();
            Assert.True(adapter.TryReadGatheringRequirement(42, out var job, out var level, out _));
            Assert.Equal(16u, job);
            Assert.Equal(10, level);
            host.GameData.Gatherables[42].GatheringType = "Harvesting";
            Assert.True(adapter.TryReadGatheringRequirement(42, out job, out _, out _));
            Assert.Equal(17u, job);
            host.GameData.Gatherables[42].GatheringType = "Fisher";
            Assert.False(adapter.TryReadGatheringRequirement(42, out _, out _, out _));
        }

        public sealed class NativeHost
        {
            public NativeManager AutoGatherListsManager { get; } = new();
            public NativeGameData GameData { get; } = new();
            public bool Current = true, Enabled, Waiting, ThrowOnEnable, EnableBeforeThrow, ThrowOnDisable;
            public List<bool> EnabledCalls { get; } = [];
            internal DadGatherBuddyListAdapter Adapter() => new(this, () => Current, () => Enabled, value =>
            {
                EnabledCalls.Add(value);
                if (!value && ThrowOnDisable) throw new InvalidOperationException("disable failed");
                if (value && ThrowOnEnable)
                {
                    if (EnableBeforeThrow) Enabled = true;
                    throw new InvalidOperationException("enable failed");
                }
                Enabled = value;
            }, () => Waiting);
            public GatherBuddy.AutoGather.Lists.AutoGatherList ExistingList(bool enabled)
            {
                var list = new GatherBuddy.AutoGather.Lists.AutoGatherList { Enabled = enabled, Name = "User" };
                AutoGatherListsManager.AddList(list);
                return list;
            }
        }

        public sealed class NativeGameData
        {
            public Dictionary<uint, NativeGatherable> Gatherables { get; } = new()
            {
                [42] = new() { ItemId = 42 }, [43] = new() { ItemId = 43 },
            };
        }

        public sealed class NativeGatherable
        {
            public uint ItemId { get; init; }
            public long CarriedCount { get; set; } = 10;
            public string GatheringType { get; set; } = "Mining";
            public int Level { get; set; } = 10;
            public List<NativeLocation> Locations { get; } = [new() { Id = 7 }];
        }
        public sealed class NativeLocation { public uint Id { get; init; } }

        public sealed class NativeManager
        {
            public List<GatherBuddy.AutoGather.Lists.AutoGatherList> Lists { get; } = [];
            public List<(NativeGatherable, uint)> ActiveItems { get; } = [];
            public bool CorruptActive, ThrowOnDelete;
            public void AddList(GatherBuddy.AutoGather.Lists.AutoGatherList list, object? folder = null) => Lists.Add(list);
            public void DeleteList(GatherBuddy.AutoGather.Lists.AutoGatherList list)
            {
                if (ThrowOnDelete) throw new InvalidOperationException("delete failed");
                Lists.Remove(list);
            }
            public void SetActiveItems(bool force = false)
            {
                ActiveItems.Clear();
                foreach (var group in Lists.Where(list => list.Enabled).SelectMany(list => list.Quantities).GroupBy(pair => pair.Key))
                    ActiveItems.Add((group.Key, (uint)group.Sum(pair => pair.Value) + (CorruptActive ? 1u : 0u)));
            }
            public void Save() { }
        }
    }
}

// External GBR contract doubles. Native list state and enabled-list quantity summing are
// deliberately independent of DAD's adapter and planning code.
namespace GatherBuddy.AutoGather.Lists
{
    public sealed class AutoGatherList
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public bool Enabled { get; set; }
        public bool Fallback { get; set; }
        public bool RemoveCompletedItems { get; set; }
        internal bool UsesRetainerInventory { get; set; } = true;
        public Dictionary<dad.Tests.DadGatherBuddyListAdapterTests.NativeGatherable, uint> Quantities { get; } = [];
        public Dictionary<dad.Tests.DadGatherBuddyListAdapterTests.NativeGatherable, dad.Tests.DadGatherBuddyListAdapterTests.NativeLocation> PreferredLocations { get; } = [];
        public bool Add(dad.Tests.DadGatherBuddyListAdapterTests.NativeGatherable item, uint quantity)
            => Quantities.TryAdd(item, Math.Clamp(quantity, 1u, 999999u));
        public bool SetPreferredLocation(dad.Tests.DadGatherBuddyListAdapterTests.NativeGatherable item, dad.Tests.DadGatherBuddyListAdapterTests.NativeLocation location)
            => PreferredLocations.TryAdd(item, location);
    }
}

namespace GatherBuddy.AutoGather.Extensions
{
    public static class GatherableExtensions
    {
        public static long GetInventoryCount(dad.Tests.DadGatherBuddyListAdapterTests.NativeGatherable item) => item.CarriedCount;
        public static long GetTotalCount(dad.Tests.DadGatherBuddyListAdapterTests.NativeGatherable item) => 9999;
    }
}
