using System.Reflection;
using dad.Models;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace dad.Services;

// Small host binding for the current GBR IPC v2 and native list surface. Called only
// on DAD's existing framework-thread action/probe/status cadence, never from Draw.
public sealed class DadGatherBuddyService : IDadGatherClientService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DadPresenceService presence;
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<bool> enabled;
    private readonly ICallGateSubscriber<bool, object> setEnabled;
    private readonly ICallGateSubscriber<bool> waiting;
    private static readonly InventoryType[] CarriedContainers =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.Crystals];

    public DadGatherBuddyService(IDalamudPluginInterface pluginInterface, DadPresenceService presence)
    {
        this.pluginInterface = pluginInterface;
        this.presence = presence;
        version = pluginInterface.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
        enabled = pluginInterface.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled");
        setEnabled = pluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled");
        waiting = pluginInterface.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherWaiting");
    }

    internal bool TryCreateSession(out DadGatherBuddyListAdapter? session, out string error)
    {
        session = null;
        error = string.Empty;
        try
        {
            if (version.InvokeFunc() != 2) throw new InvalidOperationException("GBR IPC v2 is required for gathering.");
            var root = FindRoot(out var rootPath) ?? throw new InvalidOperationException("The loaded GBR plugin instance is unavailable.");
            session = new(root, () => ReferenceEquals(ReadRootPath(rootPath), root), () => enabled.InvokeFunc(),
                value => setEnabled.InvokeAction(value), () => waiting.InvokeFunc(), InventoryReady);
            return true;
        }
        catch (Exception exception)
        {
            error = "GBR gathering is unavailable: " + exception.GetBaseException().Message;
            return false;
        }
    }

    bool IDadGatherClientService.TryCreateSession(out IDadGatherClientSession? session, out string error)
    {
        var ready = TryCreateSession(out var adapter, out error);
        session = adapter;
        return ready;
    }

    public DadGatherStockReply Probe(DadGatherStockQuery query)
    {
        var reply = DadGatherStockReply.Reject(query, string.Empty);
        var runtime = presence.BuildLiveSafetySnapshot();
        if (string.IsNullOrWhiteSpace(query.RequestId) || string.IsNullOrWhiteSpace(query.RunId) ||
            string.IsNullOrWhiteSpace(query.SlotId) || query.WorkerSessionId.IsEmpty || query.AccountKey.IsEmpty ||
            query.CharacterKey.IsEmpty || query.ItemIds == null || query.ItemIds.Count is < 1 or > DadGatherPartyPlanningRules.MaximumItems ||
            query.ItemIds.Any(id => id == 0) || query.ItemIds.Distinct().Count() != query.ItemIds.Count ||
            query.RequestedAtUtc > DateTime.UtcNow.AddSeconds(5) || DateTime.UtcNow - query.RequestedAtUtc > TimeSpan.FromMinutes(2))
        {
            reply.Error = "Gathering stock query is incomplete or stale.";
            return reply;
        }
        if (query.WorkerSessionId != runtime.WorkerSessionId ||
            !DadRosterIdentity.SameAccount(query.AccountKey, runtime.ManagedAccountKey) ||
            !string.Equals(query.CharacterKey.Value, runtime.ActiveCharacterKey.Value, StringComparison.OrdinalIgnoreCase) ||
            !runtime.WorldReadyStable || !runtime.IsAvailable || !runtime.IsEligibleForRun || !InventoryReady())
        {
            reply.Error = "Gathering stock query does not match a ready local worker/account/character.";
            return reply;
        }
        if (!TryCreateSession(out var native, out var error)) { reply.Error = error; return reply; }
        try
        {
            if (enabled.InvokeFunc()) { reply.Error = "GBR is already running independently."; return reply; }
            var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
            foreach (var itemId in query.ItemIds)
            {
                if (!native!.TryReadCarriedCount(itemId, out var count, out error)) { reply.Error = error; return reply; }
                reply.CarriedCounts[itemId] = count;
                if (native.TryReadGatheringRequirement(itemId, out var jobId, out var level, out _) &&
                    Plugin.PlayerState.IsLoaded && jobs.TryGetRow(jobId, out var job) && Plugin.PlayerState.GetClassJobLevel(job) >= level)
                    reply.EligibleItems.Add(itemId);
            }
            reply.Available = true;
            reply.ObservedAtUtc = DateTime.UtcNow;
        }
        catch (Exception exception) { reply.Error = exception.GetBaseException().Message; }
        return reply;
    }

    private static unsafe bool InventoryReady()
    {
        if (!Plugin.PlayerState.IsLoaded) return false;
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;
        foreach (var type in CarriedContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return false;
        }
        return true;
    }

    private object? ReadRootPath(IReadOnlyList<MemberInfo> path)
    {
        object? value = pluginInterface.InstalledPlugins.FirstOrDefault(plugin => plugin.IsLoaded &&
            string.Equals(plugin.InternalName, "GatherBuddyReborn", StringComparison.OrdinalIgnoreCase));
        foreach (var member in path)
        {
            if (value == null || !member.DeclaringType!.IsInstanceOfType(value)) return null;
            value = member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value);
        }
        return value;
    }

    private object? FindRoot(out MemberInfo[] path)
    {
        path = [];
        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (!plugin.IsLoaded || !string.Equals(plugin.InternalName, "GatherBuddyReborn", StringComparison.OrdinalIgnoreCase)) continue;
            var found = FindInstance(plugin, 3, new HashSet<object>(ReferenceEqualityComparer.Instance), out path);
            if (found != null) return found;
        }
        return null;
    }

    private static object? FindInstance(object value, int depth, HashSet<object> visited, out MemberInfo[] path)
    {
        path = [];
        var type = value.GetType();
        if (type.FullName is "GatherBuddy.Plugin.GatherBuddy" or "GatherBuddy.GatherBuddy") return value;
        if (depth <= 0 || type.IsValueType || value is string || !visited.Add(value)) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in type.GetFields(flags))
        {
            var member = field.GetValue(value);
            if (member != null && FindInstance(member, depth - 1, visited, out var nested) is { } result)
            {
                path = [field, ..nested];
                return result;
            }
        }
        foreach (var property in type.GetProperties(flags).Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            object? member;
            try { member = property.GetValue(value); } catch { continue; }
            if (member != null && FindInstance(member, depth - 1, visited, out var nested) is { } result)
            {
                path = [property, ..nested];
                return result;
            }
        }
        return null;
    }
}
