using dad.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace dad.Services;

internal interface IDadClassJobGearsetGateway
{
    DadClassJobGearsetCatalogSnapshot ReadCatalog();

    DadClassJobEquipAttemptResult TryEquip(int gearsetId, uint expectedJobId);

    DadClassJobEquipAttemptResult TryEquipSoulCrystal(int gearsetId, uint expectedJobId);

    DadClassJobEquipAttemptResult TryUpdatePromotedGearset(int gearsetId, uint expectedJobId);
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

            var promotions = Plugin.DataManager.GetExcelSheet<ClassJob>()
                .Where(row => row.ClassJobParent.RowId != 0 && row.ClassJobParent.RowId != row.RowId && row.ItemSoulCrystal.RowId != 0)
                .Select(row => new DadClassJobPromotionSnapshot(row.RowId, row.ClassJobParent.RowId, row.ItemSoulCrystal.RowId));
            return DadClassJobGearsetCatalogSnapshot.Success(gearsets, promotions);
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

            if (entry->ClassJob != expectedJobId &&
                (!TryGetPromotion(expectedJobId, out var promotion) || entry->ClassJob != promotion.BaseClassJobId))
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

    public DadClassJobEquipAttemptResult TryEquipSoulCrystal(int gearsetId, uint expectedJobId)
    {
        RequireFrameworkThread();
        try
        {
            if (!TryGetPromotion(expectedJobId, out var promotion))
                return DadClassJobEquipAttemptResult.Rejected("The requested job has no base-class soul-crystal promotion.");
            var gearsets = RaptureGearsetModule.Instance();
            if (!IsSafePlayer() || Plugin.ObjectTable.LocalPlayer!.ClassJob.RowId != promotion.BaseClassJobId ||
                gearsets == null || gearsets->CurrentGearsetIndex != gearsetId)
                return DadClassJobEquipAttemptResult.Rejected("The exact base class and selected gearset must remain equipped before promotion.");

            var inventory = InventoryManager.Instance();
            var equipped = inventory == null ? null : inventory->GetInventoryContainer(InventoryType.EquippedItems);
            if (equipped == null || !equipped->IsLoaded || equipped->Size <= DadDutyLifecycleRules.SoulCrystalEquippedSlotIndex)
                return DadClassJobEquipAttemptResult.Rejected("Equipped inventory is unavailable for soul-crystal preparation.");
            foreach (var inventoryType in new[] { InventoryType.ArmorySoulCrystal, InventoryType.Inventory1,
                         InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
            {
                var container = inventory->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded) continue;
                for (ushort slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId != promotion.SoulCrystalItemId) continue;
                    var result = inventory->MoveItemSlot(inventoryType, slot, InventoryType.EquippedItems,
                        DadDutyLifecycleRules.SoulCrystalEquippedSlotIndex, true);
                    return result == 0 ? DadClassJobEquipAttemptResult.Success() :
                        DadClassJobEquipAttemptResult.Rejected($"The game rejected soul-crystal equip with result {result}.");
                }
            }
            return DadClassJobEquipAttemptResult.Rejected("The requested job's soul crystal is absent from the Armoury Chest and inventory.");
        }
        catch (Exception ex)
        {
            return DadClassJobEquipAttemptResult.Rejected($"Soul-crystal equip threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    public DadClassJobEquipAttemptResult TryUpdatePromotedGearset(int gearsetId, uint expectedJobId)
    {
        RequireFrameworkThread();
        try
        {
            if (!IsSafePlayer() || Plugin.ObjectTable.LocalPlayer!.ClassJob.RowId != expectedJobId ||
                !TryGetPromotion(expectedJobId, out var promotion))
                return DadClassJobEquipAttemptResult.Rejected("The requested promoted job is not safely equipped; the gearset was not updated.");
            var gearsets = RaptureGearsetModule.Instance();
            var inventory = InventoryManager.Instance();
            var crystal = inventory == null ? null : inventory->GetInventorySlot(InventoryType.EquippedItems,
                DadDutyLifecycleRules.SoulCrystalEquippedSlotIndex);
            var entry = gearsets == null || gearsetId is < 0 or >= GearsetCapacity ? null : gearsets->GetGearset(gearsetId);
            if (gearsets == null || gearsets->CurrentGearsetIndex != gearsetId || entry == null ||
                (entry->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 || !gearsets->IsValidGearset(gearsetId) ||
                (entry->ClassJob != promotion.BaseClassJobId && entry->ClassJob != expectedJobId) ||
                crystal == null || crystal->ItemId != promotion.SoulCrystalItemId)
                return DadClassJobEquipAttemptResult.Rejected("The selected gearset or equipped soul crystal changed; the gearset was not updated.");

            _ = gearsets->UpdateGearset(gearsetId);
            entry = gearsets->GetGearset(gearsetId);
            return entry != null && entry->ClassJob == expectedJobId &&
                   entry->Items[DadDutyLifecycleRules.SoulCrystalEquippedSlotIndex].ItemId == promotion.SoulCrystalItemId
                ? DadClassJobEquipAttemptResult.Success()
                : DadClassJobEquipAttemptResult.Rejected("The promoted job and soul crystal were not saved in the selected gearset.");
        }
        catch (Exception ex)
        {
            return DadClassJobEquipAttemptResult.Rejected($"Updating the promoted gearset threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryGetPromotion(uint jobId, out DadClassJobPromotionSnapshot promotion)
    {
        promotion = default;
        if (!Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(jobId, out var row) ||
            row.ClassJobParent.RowId == 0 || row.ClassJobParent.RowId == jobId || row.ItemSoulCrystal.RowId == 0)
            return false;
        promotion = new(row.RowId, row.ClassJobParent.RowId, row.ItemSoulCrystal.RowId);
        return true;
    }

    private static bool IsSafePlayer()
        => Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null &&
           !Plugin.Condition[ConditionFlag.InCombat] && !Plugin.Condition[ConditionFlag.BoundByDuty] &&
           !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51];

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException(
                "RaptureGearsetModule may only be accessed on the Dalamud framework thread.");
        }
    }
}
