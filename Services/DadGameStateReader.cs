using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;

namespace dad.Services;

// Feature batch A (dadfeatures20260620b): tiny guarded reads of live game state used by stop conditions.
// Callers must already be on the Dalamud framework thread (stop evaluation runs from the coordinator update).
internal static unsafe class DadGameStateReader
{
    public static uint GetFourPlayerDungeonContentFinderConditionId(uint expectedContentFinderConditionId)
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null
                || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
                return 0;
            var gameMain = GameMain.Instance();
            if (gameMain == null || gameMain->CurrentContentFinderConditionId == 0
                || gameMain->CurrentTerritoryTypeId == 0
                || gameMain->CurrentTerritoryTypeId != Plugin.ClientState.TerritoryType
                || expectedContentFinderConditionId != 0 && expectedContentFinderConditionId != gameMain->CurrentContentFinderConditionId)
                return 0;
            var row = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>()?.GetRowOrDefault(gameMain->CurrentContentFinderConditionId);
            if (row is not { } duty)
                return 0;
            var members = duty.ContentMemberType.ValueNullable;
            return duty.TerritoryType.RowId == gameMain->CurrentTerritoryTypeId
                && duty.ContentType.RowId == 2 && members?.MembersPerParty == 4 && members?.PartyCount == 1
                ? gameMain->CurrentContentFinderConditionId : 0u;
        }
        catch { return 0; }
    }

    public static int GetInventoryItemCount(uint itemId)
    {
        if (itemId == 0)
            return 0;

        try
        {
            var manager = InventoryManager.Instance();
            return manager == null ? 0 : manager->GetInventoryItemCount(itemId);
        }
        catch
        {
            return 0;
        }
    }

    public static uint? GetRestedExperience()
    {
        try
        {
            var hud = AgentHUD.Instance();
            return hud == null ? null : hud->ExpRestedExperience;
        }
        catch
        {
            return null;
        }
    }
}
