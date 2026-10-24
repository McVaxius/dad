using dad.Models;

namespace dad.Services;

internal static class DadTrustNpcCatalog
{
    // Current provider evidence: erdelf/AutoDuty c62f8a1e, TrustHelper.PopulateTrustMembers,
    // reviewed 2026-10-23. Native slots are not identities; variants share one NPC.
    internal static bool TryResolve(uint nativeMemberId, uint classJobId, out DadTrustNpcId npc)
    {
        npc = classJobId == 0 ? DadTrustNpcId.Unknown : nativeMemberId switch
        {
            0 or 6 => DadTrustNpcId.Alphinaud,
            1 => DadTrustNpcId.Alisaie,
            2 => DadTrustNpcId.Thancred,
            3 => DadTrustNpcId.Urianger,
            4 => DadTrustNpcId.Yshtola,
            5 => DadTrustNpcId.Ryne,
            7 => DadTrustNpcId.Estinien,
            8 or 9 or 10 => DadTrustNpcId.GrahaTia,
            103 => DadTrustNpcId.Zero,
            11 => DadTrustNpcId.Krile,
            _ => DadTrustNpcId.Unknown,
        };
        return npc != DadTrustNpcId.Unknown;
    }

    internal static int? SupportedCap(DadTrustNpcId npc, int observedLevel)
    {
        var cap = npc switch { DadTrustNpcId.Ryne => 80, DadTrustNpcId.Zero => 90, _ => 100 };
        return observedLevel > cap ? null : cap;
    }

    internal static uint UnlockQuest(DadTrustNpcId npc) => npc == DadTrustNpcId.GrahaTia ? 69318u : 0u;
}
