using dad.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace dad.Services;

internal interface IDadClassJobGearsetGateway
{
    DadClassJobGearsetCatalogSnapshot ReadCatalog();

    DadClassJobEquipAttemptResult TryEquip(int gearsetId, uint expectedJobId);
}

// The only unsafe boundary for requested-job preparation. Callers must still supply the gate with
// a safe-to-equip observation before invoking TryEquip.
internal sealed unsafe class DadClassJobGearsetGateway(IFramework framework) : IDadClassJobGearsetGateway
{
    private const int GearsetCapacity = 100;

    public DadClassJobGearsetCatalogSnapshot ReadCatalog()
    {
        RequireFrameworkThread();

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return DadClassJobGearsetCatalogSnapshot.Unavailable("RaptureGearsetModule is unavailable.");

            var gearsets = new List<DadClassJobGearsetSnapshot>(GearsetCapacity);
            for (var gearsetId = 0; gearsetId < GearsetCapacity; gearsetId++)
            {
                var entry = module->GetGearset(gearsetId);
                if (entry == null)
                    continue;

                var exists = (entry->Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0;
                var valid = exists && module->IsValidGearset(gearsetId);
                gearsets.Add(new DadClassJobGearsetSnapshot(
                    gearsetId,
                    entry->ClassJob,
                    exists,
                    valid));
            }

            return DadClassJobGearsetCatalogSnapshot.Success(gearsets);
        }
        catch (Exception ex)
        {
            return DadClassJobGearsetCatalogSnapshot.Unavailable(
                $"Reading RaptureGearsetModule failed with {ex.GetType().Name}: {ex.Message}");
        }
    }

    public DadClassJobEquipAttemptResult TryEquip(int gearsetId, uint expectedJobId)
    {
        RequireFrameworkThread();

        if (gearsetId is < 0 or >= GearsetCapacity)
            return DadClassJobEquipAttemptResult.Rejected($"Gearset ID {gearsetId} is outside the valid range.");

        if (expectedJobId is 0 or > byte.MaxValue)
            return DadClassJobEquipAttemptResult.Rejected($"Class/job ID {expectedJobId} is invalid.");

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return DadClassJobEquipAttemptResult.Rejected("RaptureGearsetModule is unavailable.");

            var entry = module->GetGearset(gearsetId);
            if (entry == null ||
                (entry->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 ||
                !module->IsValidGearset(gearsetId))
            {
                return DadClassJobEquipAttemptResult.Rejected($"Gearset {gearsetId} is no longer valid.");
            }

            if (entry->ClassJob != expectedJobId)
            {
                return DadClassJobEquipAttemptResult.Rejected(
                    $"Gearset {gearsetId} is class/job {entry->ClassJob}, not requested class/job {expectedJobId}.");
            }

            var result = module->EquipGearset(gearsetId, 0);
            return result == 0
                ? DadClassJobEquipAttemptResult.Success()
                : DadClassJobEquipAttemptResult.Rejected(
                    $"EquipGearset rejected gearset {gearsetId} with result {result}.");
        }
        catch (Exception ex)
        {
            return DadClassJobEquipAttemptResult.Rejected(
                $"EquipGearset threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException(
                "RaptureGearsetModule may only be accessed on the Dalamud framework thread.");
        }
    }
}
