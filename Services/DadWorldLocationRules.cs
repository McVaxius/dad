namespace dad.Services;

internal readonly record struct DadDataCenterLocation(
    uint DataCenterId, string DataCenterName, uint RegionId, string RegionName);

internal static class DadWorldLocationRules
{
    public static bool TryResolveDataCenter(
        uint logicalDataCenterId,
        uint worldRegionId,
        Func<uint, DadDataCenterLocation?> lookup,
        out DadDataCenterLocation location)
    {
        location = default;
        if (logicalDataCenterId == 0 || worldRegionId == 0)
            return false;

        var resolved = lookup(logicalDataCenterId);
        if (resolved is not { } candidate ||
            candidate.DataCenterId != logicalDataCenterId ||
            candidate.RegionId != worldRegionId ||
            string.IsNullOrWhiteSpace(candidate.DataCenterName) ||
            string.IsNullOrWhiteSpace(candidate.RegionName))
            return false;

        location = candidate with
        {
            DataCenterName = candidate.DataCenterName.Trim(),
            RegionName = candidate.RegionName.Trim(),
        };
        return true;
    }
}
