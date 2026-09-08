using dad.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace dad.Services;

public sealed record DadDutySupportLevelingPreset(uint TerritoryType, string Name, bool ManualOnly = false);

public sealed record DadDutyLevelingPlayer(uint JobId, int Level, int ItemLevel, string Blocker = "");

public static class DadDutySupportLevelingPresets
{
    // AutoDuty Helpers/LevelingHelper.cs, erdelf/AutoDuty, reviewed 2026-09-08.
    // These are territory IDs, never ContentFinderCondition IDs. Use the standard route;
    // the cutscene-specific alternative is an explicit manual choice only.
    public static IReadOnlyList<DadDutySupportLevelingPreset> Entries { get; } = Array.AsReadOnly<DadDutySupportLevelingPreset>(
    [
        new(1036, "Sastasha"), new(1037, "The Tam-Tara Deepcroft"),
        new(1039, "The Thousand Maws of Toto-Rak"), new(1041, "Brayflox's Longstop"),
        new(1303, "Cutter's Cry"), new(1042, "The Stone Vigil"),
        new(1330, "Dzemael Darkhold"), new(1331, "The Aurum Vale"),
        new(1043, "Castrum Meridianum"), new(1366, "The Dusk Vigil"),
        new(1064, "Sohm Al"), new(1065, "The Aery"), new(1066, "The Vault"),
        new(1109, "The Great Gubal Library"), new(1142, "The Sirensong Sea"),
        new(1367, "Shisui of the Violet Tides"), new(1144, "Doma Castle"), new(1145, "Castrum Abania"),
        new(837, "Holminster Switch"), new(821, "Dohn Mheg"), new(823, "The Qitana Ravel"),
        new(836, "Malikah's Well"), new(822, "Mt. Gulg"), new(952, "The Tower of Zot"),
        new(969, "The Tower of Babil"), new(970, "Vanaspati"), new(974, "Ktisis Hyperboreia"),
        new(978, "The Aitiascope"), new(1167, "Ihuykatumu"), new(1193, "Worqor Zormor"),
        new(1194, "The Skydeep Cenote"), new(1198, "Vanguard"), new(1208, "Origenics"),
        new(1048, "The Porta Decumana (cutscene route)", ManualOnly: true),
    ]);

    public static DadPlannerDutyOption? Resolve(DadDutySupportLevelingPreset entry,
        Func<uint, IReadOnlyList<DadPlannerDutyOption>> catalog, out string blocker)
    {
        var candidates = catalog(entry.TerritoryType)
            .Where(duty => duty.TerritoryType == entry.TerritoryType && duty.ContentFinderConditionId != 0 && duty.SupportsDutySupport).ToArray();
        blocker = candidates.Length switch
        {
            0 => $"{entry.Name}: territory {entry.TerritoryType} has no Duty Support entry in the duty catalog.",
            > 1 => $"{entry.Name}: territory {entry.TerritoryType} maps to multiple Duty Support duties.",
            _ => "",
        };
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static DadPlannerDutyOption? Select(DadDutyLevelingPlayer player,
        Func<uint, IReadOnlyList<DadPlannerDutyOption>> catalog, Func<uint, bool?> unlocked, out string blocker)
    {
        blocker = player.Blocker;
        if (blocker.Length != 0) return null;
        if (player.JobId == 0 || player.Level <= 0 || player.ItemLevel <= 0)
            blocker = "Current job, actual level, or equipped item level is unavailable.";
        else if (!DadRosterCharacterMerge.IsCombatJob(player.JobId))
            blocker = "Duty Support leveling requires a combat class or job; limited jobs are unsupported.";
        else if (player.JobId == 36)
            blocker = "Blue Mage cannot use Duty Support leveling.";
        else if (player.Level < 15)
            blocker = $"Duty Support leveling starts at level 15; the current class/job is level {player.Level}.";
        if (blocker.Length != 0) return null;

        var eligible = new List<DadPlannerDutyOption>();
        var reasons = new List<string>();
        var mappingReasons = new List<string>();
        foreach (var entry in Entries.Where(entry => !entry.ManualOnly))
        {
            var duty = Resolve(entry, catalog, out var mappingBlocker);
            if (duty == null) { mappingReasons.Add(mappingBlocker); continue; }
            if (duty.JobLevelRequired <= 0) { reasons.Add($"{entry.Name}: required level is unavailable."); continue; }
            if (duty.JobLevelRequired > player.Level) continue;
            if (duty.ItemLevelRequired > player.ItemLevel)
            { reasons.Add($"{entry.Name}: needs item level {duty.ItemLevelRequired} (equipped {player.ItemLevel})."); continue; }
            var isUnlocked = unlocked(duty.ContentFinderConditionId);
            if (isUnlocked == null)
            {
                blocker = $"{entry.Name}: duty unlock evidence is unavailable; cannot select the highest eligible leveling dungeon.";
                return null;
            }
            if (isUnlocked != true)
            { reasons.Add($"{entry.Name}: {(isUnlocked == false ? "duty is locked" : "duty unlock evidence is unavailable")}."); continue; }
            eligible.Add(duty);
        }
        var selected = eligible.OrderByDescending(duty => duty.JobLevelRequired)
            .ThenByDescending(duty => duty.ItemLevelRequired).ThenByDescending(duty => duty.ContentFinderConditionId).FirstOrDefault();
        if (selected == null)
            blocker = $"No eligible curated Duty Support dungeon for job {player.JobId}, level {player.Level}, item level {player.ItemLevel}. " +
                      string.Join(" ", (reasons.Count > 0 ? reasons : mappingReasons).Take(3));
        return selected;
    }

    public static DadPlannerGroup CreateManual(DadPlannerDutyOption duty, DadAcquiredCharacter character)
        => new()
        {
            DisplayName = $"Leveling - {duty.DutyDisplayName}",
            ActivityMode = DadPlannerActivityMode.DutySupport,
            OperatorMode = DadPlannerOperatorMode.RemotePartyPlan,
            DutyContentFinderConditionId = duty.ContentFinderConditionId,
            DutyDisplayName = duty.DutyDisplayName,
            DutyExpectedPartySize = 1,
            StopPolicy = new() { Mode = DadPlannerStopMode.AfterRuns, AfterRuns = 1 },
            Slots = [new() { SlotId = DadPlannerSlotRules.LeaderSlotId,
                RequiredAccountKey = character.AccountId, RequiredCharacterKey = character.CharacterKey,
                WakePolicy = DadSchedulerWakePolicy.AlreadyOnlineOnly, AllowSubstitution = false }],
        };

    internal static unsafe DadDutyLevelingPlayer ReadPlayer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var state = PlayerState.Instance();
        var inventory = InventoryManager.Instance();
        if (!Plugin.ClientState.IsLoggedIn || player == null || !player.ClassJob.IsValid || state == null || inventory == null)
            return new(0, 0, 0, "Current character, job, or equipped inventory is unavailable.");
        var job = player.ClassJob.Value;
        if (job.ExpArrayIndex < 0 || job.ExpArrayIndex >= state->ClassJobLevels.Length)
            return new(job.RowId, 0, 0, "Current job's actual level is unavailable.");
        var equipment = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipment == null || !equipment->IsLoaded || equipment->Size < 13)
            return new(job.RowId, 0, 0, "Equipped inventory is not loaded.");
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        uint total = 0;
        for (var slot = 0; slot < 13; slot++)
        {
            if (slot == 5) continue; // Retired belt slot. Soul crystal is slot 13.
            var equipped = equipment->GetInventorySlot(slot);
            if (equipped == null) return new(job.RowId, 0, 0, "An equipped inventory slot is unreadable.");
            if (equipped->ItemId == 0)
            {
                if (slot == 0) return new(job.RowId, 0, 0, "The equipped main-hand weapon is unavailable.");
                continue;
            }
            var item = items.GetRowOrDefault(equipped->GetItemId());
            if (item == null || !item.Value.LevelItem.IsValid || !item.Value.EquipSlotCategory.IsValid)
                return new(job.RowId, 0, 0, "An equipped item's level or slot category is unavailable.");
            var slots = item.Value.EquipSlotCategory.Value;
            // Negative slot flags are occupied by this item too: two-handed weapons
            // and ARR armor that covers multiple slots contribute for every covered slot.
            var weight = 1 + (slots.OffHand < 0 ? 1 : 0) + (slots.Head < 0 ? 1 : 0) +
                         (slots.Body < 0 ? 1 : 0) + (slots.Gloves < 0 ? 1 : 0) +
                         (slots.Legs < 0 ? 1 : 0) + (slots.Feet < 0 ? 1 : 0);
            total += item.Value.LevelItem.RowId * (uint)weight;
        }
        return new(job.RowId, state->ClassJobLevels[job.ExpArrayIndex], (int)(total / 12));
    }

    internal static unsafe bool? ReadUnlocked(uint cfcId)
    {
        var duty = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId);
        if (!Plugin.ClientState.IsLoggedIn || UIState.Instance() == null || duty == null || duty.Value.Content.RowId == 0)
            return null;
        return UIState.IsInstanceContentUnlocked(duty.Value.Content.RowId);
    }
}
