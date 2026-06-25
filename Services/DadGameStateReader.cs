using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace dad.Services;

// Feature batch A (dadfeatures20260620b): tiny guarded reads of live game state used by stop conditions.
// Callers must already be on the Dalamud framework thread (stop evaluation runs from the coordinator update).
internal static unsafe class DadGameStateReader
{
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
