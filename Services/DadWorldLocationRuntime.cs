using Lumina.Excel.Sheets;
using dad.Models;

namespace dad.Services;

internal static class DadWorldLocationRuntime
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<uint, DadWorldLocationObservation> WorldCache = [];
    internal static Func<DateTime, DadWorldLocationObservation?>? CurrentObservation { get; set; }
    internal static Func<uint, DateTime, DadWorldLocationObservation?>? WorldObservation { get; set; }

    internal static void ClearCache()
    {
        lock (CacheGate)
            WorldCache.Clear();
    }

    public static DadWorldLocationObservation? CaptureCurrent(DateTime observedAtUtc)
    {
        if (CurrentObservation != null) return CurrentObservation(observedAtUtc)?.Clone();
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (!Plugin.ClientState.IsLoggedIn || player == null)
                return null;

            return TryResolveWorld((uint)player.CurrentWorld.RowId, observedAtUtc, out var location)
                ? location
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryResolveWorld(
        uint worldId,
        DateTime observedAtUtc,
        out DadWorldLocationObservation location)
    {
        location = new DadWorldLocationObservation { ObservedAtUtc = NormalizeUtc(observedAtUtc) };
        if (WorldObservation != null)
        {
            var observed = WorldObservation(worldId, observedAtUtc);
            if (observed == null) return false;
            location = observed.Clone();
            return location.IsComplete;
        }
        lock (CacheGate)
        {
            if (WorldCache.TryGetValue(worldId, out var cached))
            {
                location = cached.Clone();
                location.ObservedAtUtc = NormalizeUtc(observedAtUtc);
                return true;
            }
        }

        try
        {
            var worldSheet = Plugin.DataManager.GetExcelSheet<World>();
            if (worldId == 0 || worldSheet == null || !worldSheet.TryGetRow(worldId, out var world))
                return false;

            if (world.DataCenter.RowId == 0 || !world.DataCenter.IsValid)
                return false;
            var parent = world.DataCenter.Value;
            if (parent.Region.RowId == 0 || !parent.Region.IsValid)
                return false;
            var dataCenters = Plugin.DataManager.GetExcelSheet<WorldDCGroupType>();
            if (dataCenters == null || !DadWorldLocationRules.TryResolveDataCenter(
                    // World.DataCenter can represent the shared regional group. Unknown0 is
                    // the logical DC used by the lobby and current Lifestream login selection.
                    world.Unknown0,
                    parent.Region.RowId,
                    id => dataCenters.TryGetRow(id, out var row) && row.Region.IsValid
                        ? new DadDataCenterLocation(id, row.Name.ToString(), row.Region.RowId,
                            row.Region.Value.Name.ToString())
                        : null,
                    out var dataCenter))
                return false;
            location = new DadWorldLocationObservation
            {
                WorldId = worldId,
                WorldName = world.Name.ToString().Trim(),
                DataCenterId = dataCenter.DataCenterId,
                DataCenterName = dataCenter.DataCenterName,
                RegionId = dataCenter.RegionId,
                RegionName = dataCenter.RegionName,
                ObservedAtUtc = NormalizeUtc(observedAtUtc),
            };
            if (!location.IsComplete)
                return false;
            lock (CacheGate)
                WorldCache[worldId] = location.Clone();
            return true;
        }
        catch
        {
            location = new DadWorldLocationObservation { ObservedAtUtc = NormalizeUtc(observedAtUtc) };
            return false;
        }
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
