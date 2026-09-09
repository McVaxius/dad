using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace dad.Services;

// One attempt per duty, advanced only by the existing framework update loop.
public sealed class DadLevelingGearPreparation
{
    private readonly IPluginLog log;
    private readonly Action calculate;
    private readonly Func<bool> isUpdating;
    private readonly Action equip;
    private readonly Action updateGearset;
    private int stage;
    private DateTime? startedAtUtc;

    public DadLevelingGearPreparation(IPluginLog log) : this(log, new NativeGear()) { }

    private DadLevelingGearPreparation(IPluginLog log, NativeGear native)
        : this(log, native.Calculate, native.IsUpdating, native.Equip, native.UpdateGearset) { }

    internal DadLevelingGearPreparation(IPluginLog log, Action calculate, Func<bool> isUpdating,
        Action equip, Action updateGearset)
    {
        this.log = log;
        this.calculate = calculate;
        this.isUpdating = isUpdating;
        this.equip = equip;
        this.updateGearset = updateGearset;
    }

    public void Reset(bool enabled = false)
    {
        stage = enabled ? 1 : 0;
        startedAtUtc = null;
    }

    public bool Update(DateTime nowUtc, bool allowActions = true)
    {
        if (stage == 0) return true;
        if (!allowActions && !startedAtUtc.HasValue) return false;
        startedAtUtc ??= nowUtc;
        try
        {
            if (nowUtc - startedAtUtc.Value >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Preparation timed out after five seconds.");
            if (!allowActions) return false;
            if (stage == 1)
            {
                calculate();
                stage = 2;
                return false;
            }
            if (isUpdating()) return false;
            if (stage == 2)
            {
                // Mark before invoking: even a throwing native action must never be replayed.
                stage = 3;
                equip();
                return false;
            }
            stage = 0;
            updateGearset();
        }
        catch (Exception ex)
        {
            stage = 0;
            log.Warning("[dad][LevelingGear] {Reason} Continuing duty without retrying gear preparation.", ex.Message);
        }
        return true;
    }

    internal static unsafe bool IsPending(bool recommendationsUpdating, InventoryContainer* equipment)
        => recommendationsUpdating || equipment == null || !equipment->IsLoaded || equipment->Size < 13 ||
           equipment->Items == null || equipment->Items[0].ItemId == 0;

    private sealed unsafe class NativeGear
    {
        private uint jobId;
        private ulong contentId;
        private int gearsetId;

        public void Calculate()
        {
            RequireSafePlayer();
            jobId = Plugin.ObjectTable.LocalPlayer!.ClassJob.RowId;
            contentId = Plugin.PlayerState.ContentId;
            var gearsets = RaptureGearsetModule.Instance();
            gearsetId = gearsets == null ? -1 : gearsets->CurrentGearsetIndex;
            var module = GetModule();
            if (module->IsUpdating)
                throw new InvalidOperationException("Native recommendations are already being calculated.");
            if (!module->SetupForClassJob((byte)jobId))
                throw new InvalidOperationException("Native recommended gear calculation was rejected.");
        }

        public bool IsUpdating()
        {
            var module = GetModule();
            var inventory = InventoryManager.Instance();
            return IsPending(module->IsUpdating,
                inventory == null ? null : inventory->GetInventoryContainer(InventoryType.EquippedItems));
        }
        public void Equip() => GetModule()->EquipRecommendedGear();

        public void UpdateGearset()
        {
            _ = GetModule();
            var module = RaptureGearsetModule.Instance();
            if (module == null || gearsetId is < 0 or >= 100 || module->CurrentGearsetIndex != gearsetId)
                throw new InvalidOperationException("The current gearset is unavailable or changed during preparation.");
            var entry = module->GetGearset(gearsetId);
            if (entry == null || (entry->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 ||
                !module->IsValidGearset(gearsetId) || entry->ClassJob != jobId)
                throw new InvalidOperationException("No valid current gearset matches the prepared job.");
            module->UpdateGearset(gearsetId);
        }

        private RecommendEquipModule* GetModule()
        {
            RequireSafePlayer();
            if (Plugin.ObjectTable.LocalPlayer!.ClassJob.RowId != jobId || Plugin.PlayerState.ContentId != contentId)
                throw new InvalidOperationException("The character or job changed during gear preparation.");
            var module = RecommendEquipModule.Instance();
            if (module == null)
                throw new InvalidOperationException("Native recommended equipment state is unavailable.");
            return module;
        }

        private static void RequireSafePlayer()
        {
            if (!Plugin.Framework.IsInFrameworkUpdateThread || !Plugin.ClientState.IsLoggedIn ||
                Plugin.ObjectTable.LocalPlayer is not { } player || !player.ClassJob.IsValid ||
                player.ClassJob.RowId is 0 or > byte.MaxValue || Plugin.Condition[ConditionFlag.InCombat] ||
                Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BetweenAreas] ||
                Plugin.Condition[ConditionFlag.BetweenAreas51])
                throw new InvalidOperationException("Native player state is unavailable for gear preparation.");
        }
    }
}
