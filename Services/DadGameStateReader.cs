using FFXIVClientStructs.FFXIV.Client.Game;

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

    // NOTE (rested-XP stop): deferred. The rested-XP field name on PlayerState differs across ClientStructs
    // versions and could not be verified against the shipped api15 build, so the RestedXpDepleted stop mode
    // is intentionally not implemented yet — adding a guessed field offset risks wrong behavior in-game.
}
