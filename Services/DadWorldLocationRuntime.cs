using Lumina.Excel.Sheets;
using dad.Models;

namespace dad.Services;

internal static class DadWorldLocationRuntime
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<uint, DadWorldLocationObservation> WorldCache = [];

    public static DadWorldLocationObservation? CaptureCurrent(DateTime observedAtUtc)
    {
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
            var dataCenter = world.DataCenter.Value;
            if (dataCenter.Region.RowId == 0 || !dataCenter.Region.IsValid)
                return false;
            var region = dataCenter.Region.Value;
            location = new DadWorldLocationObservation
            {
                WorldId = worldId,
                WorldName = world.Name.ToString().Trim(),
                DataCenterId = dataCenter.RowId,
                DataCenterName = dataCenter.Name.ToString().Trim(),
                RegionId = region.RowId,
                RegionName = region.Name.ToString().Trim(),
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
