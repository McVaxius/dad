using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using dad.Models;

namespace dad.Services;

// The host owns framework-thread dispatch, IPC readiness and the exact plugin instance.
// This adapter owns only its one native GBR list and the list flags it changes.
internal sealed class DadGatherBuddyListAdapter : IDadGatherClientSession
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly object root;
    private readonly object manager;
    private readonly Type listType;
    private readonly MethodInfo addList;
    private readonly MethodInfo deleteList;
    private readonly MethodInfo rebuild;
    private readonly MethodInfo save;
    private readonly Func<bool> currentInstance;
    private readonly Func<bool> isEnabled;
    private readonly Action<bool> setEnabled;
    private readonly Func<bool> isWaiting;
    private readonly Func<bool> inventoryReady;
    private readonly List<object> suppressed = [];
    private object? ownedList;
    private string ownedRunId = string.Empty;
    private string lastAttemptId = string.Empty;
    private DadGatherWorkerOrder[] frozenOrders = [];
    private bool accepted;

    public DadGatherBuddyListAdapter(object root, Func<bool> currentInstance, Func<bool> isEnabled, Action<bool> setEnabled,
        Func<bool>? isWaiting = null, Func<bool>? inventoryReady = null)
    {
        this.root = root;
        this.currentInstance = currentInstance;
        this.isEnabled = isEnabled;
        this.setEnabled = setEnabled;
        this.isWaiting = isWaiting ?? (() => false);
        this.inventoryReady = inventoryReady ?? (() => true);
        manager = Read(root, "AutoGatherListsManager");
        listType = root.GetType().Assembly.GetType("GatherBuddy.AutoGather.Lists.AutoGatherList", true)!;
        addList = Method(manager.GetType(), "AddList", parameters => parameters.Length == 2 &&
            parameters[0].ParameterType == listType && parameters[1].IsOptional);
        deleteList = Method(manager.GetType(), "DeleteList", parameters => parameters.Length == 1 && parameters[0].ParameterType == listType);
        rebuild = Method(manager.GetType(), "SetActiveItems", parameters => parameters.Length == 1 && parameters[0].ParameterType == typeof(bool));
        save = Method(manager.GetType(), "Save", parameters => parameters.Length == 0);
        _ = Lists();
        _ = ActiveItems();
    }

    public bool HasOwnership => ownedList != null;

    public bool TryReadActivity(string runId, out bool enabled, out bool waiting, out string error)
    {
        enabled = waiting = false;
        error = string.Empty;
        try
        {
            RequireCurrent();
            if (!accepted || runId != ownedRunId || !MatchesOwnedActiveItems())
                throw new InvalidOperationException("GBR's active gathering list no longer matches this DAD order.");
            enabled = isEnabled();
            waiting = isWaiting();
            return true;
        }
        catch (Exception exception)
        {
            error = Failure(exception);
            return false;
        }
    }

    public bool TryReadCarriedCount(uint itemId, out long count, out string error)
    {
        count = 0;
        error = string.Empty;
        try
        {
            RequireCurrent();
            if (!inventoryReady()) throw new InvalidOperationException("Carried inventory is not ready.");
            var gatherable = Gatherable(itemId);
            var extensions = root.GetType().Assembly.GetType("GatherBuddy.AutoGather.Extensions.GatherableExtensions", true)!;
            var method = extensions.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(candidate =>
                candidate.Name == "GetInventoryCount" && candidate.GetParameters() is var parameters &&
                parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(gatherable));
            count = Convert.ToInt64(method.Invoke(null, [gatherable]));
            if (count is < 0 or > int.MaxValue) throw new InvalidOperationException("GBR carried inventory count is invalid.");
            return true;
        }
        catch (Exception exception)
        {
            error = Failure(exception);
            return false;
        }
    }

    public bool TryReadGatheringRequirement(uint itemId, out uint jobId, out int level, out string error)
    {
        jobId = 0;
        level = 0;
        error = string.Empty;
        try
        {
            RequireCurrent();
            var gatherable = Gatherable(itemId);
            jobId = Read(gatherable, "GatheringType").ToString() switch
            {
                "Mining" or "Quarrying" or "Miner" => 16,
                "Logging" or "Harvesting" or "Botanist" => 17,
                _ => 0,
            };
            level = Convert.ToInt32(Read(gatherable, "Level"));
            if (jobId == 0 || level < 1 || !((IEnumerable)Read(gatherable, "Locations")).Cast<object>().Any())
                throw new InvalidOperationException($"Item {itemId} has no supported MIN/BTN gathering location.");
            return true;
        }
        catch (Exception exception)
        {
            error = Failure(exception);
            return false;
        }
    }

    public bool TryStart(string runId, IReadOnlyList<DadGatherWorkerOrder> orders, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(runId)) { error = "Gathering requires a run identity."; return false; }
        if (orders == null || orders.Any(order => order == null)) { error = "Gathering requires valid item orders."; return false; }
        if (ownedList != null)
        {
            try
            {
                if (accepted && ownedRunId == runId && frozenOrders.SequenceEqual(orders) && currentInstance()) return true;
            }
            catch (Exception exception) { error = Failure(exception); return false; }
            error = "GBR already has an owned or unsettled DAD gathering operation.";
            return false;
        }
        if (lastAttemptId == runId) { error = "This GBR gathering attempt has already ended; it will not be replayed."; return false; }
        if (orders.All(order => order.Quantity == 0)) return true;
        try
        {
            RequireCurrent();
            if (isEnabled()) throw new InvalidOperationException("GBR is already running independently. Stop it before starting DAD gathering.");
            var positive = orders.Where(order => order.Quantity > 0).ToArray();
            if (orders.Any(order => order.Quantity < 0) || positive.Length == 0 ||
                positive.Select(order => order.ItemId).Distinct().Count() != positive.Length ||
                positive.Any(order => order.ItemId == 0 || order.NativeTarget != order.StartingCount + order.Quantity ||
                    order.NativeTarget is < 1 or > DadGatherPartyPlanningRules.MaximumQuantity || order.StartingCount < 0 ||
                    string.IsNullOrWhiteSpace(order.SlotId) || string.IsNullOrWhiteSpace(order.WorkerSessionId) ||
                    string.IsNullOrWhiteSpace(order.CharacterKey)) ||
                positive.Any(order => order.SlotId != positive[0].SlotId || order.WorkerSessionId != positive[0].WorkerSessionId ||
                    order.CharacterKey != positive[0].CharacterKey))
                throw new InvalidOperationException("The GBR gathering order contains invalid or mixed client assignments.");

            var list = Activator.CreateInstance(listType) ?? throw new InvalidOperationException("GBR list construction failed.");
            Write(list, "Name", "DAD Gather " + runId);
            Write(list, "Description", "Finite DAD gathering order.");
            Write(list, "FolderPath", "DAD");
            Write(list, "Enabled", false);
            Write(list, "Fallback", false);
            Write(list, "RemoveCompletedItems", false);
            Write(list, "UsesRetainerInventory", false);
            foreach (var order in positive)
            {
                var gatherable = Gatherable(order.ItemId);
                if (!TryReadCarriedCount(order.ItemId, out var count, out error)) return false;
                if (count != order.StartingCount)
                    throw new InvalidOperationException($"Carried stock for item {order.ItemId} changed after planning. Refresh the plan.");
                var add = Method(listType, "Add", parameters => parameters.Length == 2 &&
                    parameters[0].ParameterType.IsInstanceOfType(gatherable) && parameters[1].ParameterType == typeof(uint));
                if (add.Invoke(list, [gatherable, checked((uint)order.NativeTarget)]) is not true)
                    throw new InvalidOperationException($"GBR did not add item {order.ItemId} to the owned list.");
                if (order.PreferredLocationId != 0)
                {
                    var location = ((IEnumerable)Read(gatherable, "Locations")).Cast<object>()
                        .SingleOrDefault(candidate => Convert.ToUInt32(Read(candidate, "Id")) == order.PreferredLocationId)
                        ?? throw new InvalidOperationException($"Preferred GBR location {order.PreferredLocationId} is unavailable.");
                    var setLocation = Method(listType, "SetPreferredLocation", parameters => parameters.Length == 2 &&
                        parameters[0].ParameterType.IsInstanceOfType(gatherable) && parameters[1].ParameterType.IsInstanceOfType(location));
                    if (setLocation.Invoke(list, [gatherable, location]) is not true)
                        throw new InvalidOperationException($"GBR did not accept preferred location {order.PreferredLocationId}.");
                }
            }

            // Everything above is preflight against an unregistered list. Consume the attempt before mutation.
            lastAttemptId = ownedRunId = runId;
            frozenOrders = orders.ToArray();
            ownedList = list;
            accepted = false;
            foreach (var other in Lists())
            {
                if (Read(other, "Enabled") is not true) continue;
                suppressed.Add(other);
                Write(other, "Enabled", false);
            }
            _ = addList.Invoke(manager, [list, Type.Missing]);
            Write(list, "Enabled", true);
            _ = rebuild.Invoke(manager, [false]);
            if (!MatchesOwnedActiveItems()) throw new InvalidOperationException("GBR active items do not match the exact DAD order.");
            setEnabled(true);
            if (!isEnabled()) throw new InvalidOperationException("GBR did not enable the owned gathering order.");
            accepted = true;
            return true;
        }
        catch (Exception exception)
        {
            error = Failure(exception);
            if (ownedList != null && !TryStop(runId, out var cleanup)) error += " Cleanup: " + cleanup;
            return false;
        }
    }

    public bool TryStop(string runId, out string error)
    {
        error = string.Empty;
        if (ownedList == null) return true;
        if (runId != ownedRunId) { error = "Stop does not match the owned GBR gathering run."; return false; }
        try
        {
            RequireCurrent();
            // A user-replaced active set belongs to their work. Remove our list without globally stopping it.
            if (isEnabled() && Read(ownedList, "Enabled") is true && MatchesOwnedActiveItems())
            {
                setEnabled(false);
                if (isEnabled()) throw new InvalidOperationException("GBR did not disable the owned gathering order.");
            }
            Write(ownedList, "Enabled", false);
            _ = deleteList.Invoke(manager, [ownedList]);
            var remaining = Lists();
            foreach (var other in suppressed)
                if (remaining.Any(candidate => ReferenceEquals(candidate, other)) && Read(other, "Enabled") is false)
                    Write(other, "Enabled", true);
            _ = rebuild.Invoke(manager, [false]);
            _ = save.Invoke(manager, []);
            ownedList = null;
            ownedRunId = string.Empty;
            frozenOrders = [];
            suppressed.Clear();
            accepted = false;
            return true;
        }
        catch (Exception exception)
        {
            error = Failure(exception);
            return false;
        }
    }

    private bool MatchesOwnedActiveItems()
    {
        if (ownedList == null) return false;
        if (Read(ownedList, "Enabled") is not true || Read(ownedList, "Fallback") is not false ||
            Read(ownedList, "UsesRetainerInventory") is not false ||
            Lists().Any(list => !ReferenceEquals(list, ownedList) && Read(list, "Enabled") is true)) return false;
        var expected = frozenOrders.Where(order => order.Quantity > 0).OrderBy(order => order.ItemId)
            .Select(order => (order.ItemId, Count: order.NativeTarget)).ToArray();
        return expected.SequenceEqual(ActiveItems().OrderBy(item => item.ItemId));
    }

    private (uint ItemId, long Count)[] ActiveItems()
        => ((IEnumerable)Read(manager, "ActiveItems")).Cast<object>().Select(value =>
        {
            if (value is not ITuple { Length: 2 } tuple || tuple[0] == null)
                throw new InvalidOperationException("GBR active-item tuple shape changed.");
            return (Convert.ToUInt32(Read(tuple[0]!, "ItemId")), Convert.ToInt64(tuple[1]));
        }).ToArray();

    private object[] Lists() => ((IEnumerable)Read(manager, "Lists")).Cast<object>().ToArray();

    private object Gatherable(uint itemId)
    {
        var lookup = Read(Read(root, "GameData"), "Gatherables");
        if (lookup is IDictionary dictionary && dictionary.Contains(itemId)) return dictionary[itemId]!;
        var method = Method(lookup.GetType(), "TryGetValue", parameters => parameters.Length == 2 &&
            parameters[0].ParameterType == typeof(uint) && parameters[1].IsOut);
        object?[] args = [itemId, null];
        if (method.Invoke(lookup, args) is true && args[1] != null) return args[1]!;
        throw new InvalidOperationException($"GBR has no MIN/BTN gatherable for item {itemId}.");
    }

    private void RequireCurrent()
    {
        if (!currentInstance()) throw new InvalidOperationException("The GBR plugin instance changed; no action was sent to its replacement.");
    }

    private static MethodInfo Method(Type type, string name, Func<ParameterInfo[], bool> matches)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance).SingleOrDefault(method => method.Name == name && matches(method.GetParameters()))
           ?? throw new MissingMethodException(type.FullName, name);

    private static object Read(object instance, string name)
    {
        var type = instance.GetType();
        if (type.GetProperty(name, Members) is { } property)
            return property.GetValue(property.GetMethod?.IsStatic == true ? null : instance)
                ?? throw new InvalidOperationException($"GBR member {name} is unavailable.");
        if (type.GetField(name, Members) is { } field)
            return field.GetValue(field.IsStatic ? null : instance) ?? throw new InvalidOperationException($"GBR member {name} is unavailable.");
        throw new MissingMemberException(type.FullName, name);
    }

    private static void Write(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, Members) ?? throw new MissingMemberException(instance.GetType().FullName, name);
        property.SetValue(instance, value);
    }

    private static string Failure(Exception exception) => exception.GetBaseException().Message;
}
