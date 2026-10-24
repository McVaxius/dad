using dad.Services;
using dad.Models;

namespace dad.Headless;

internal sealed class VirtualNpcDutyNative(VirtualDutyFinder game, Func<uint> jobId, Action<string> observe,
    Func<string, Exception> unexpected) : IDadNpcDutyNativeAccess
{
    private readonly HashSet<DadNpcDutyQueueMode> open = [];
    private readonly Dictionary<DadNpcDutyQueueMode, uint> selected = [];
    private readonly HashSet<byte> party = [];
    public int AlphinaudLevel { get; set; } = 100;
    public uint LocalJobId => game.HasLocalPlayer ? jobId() : 0;
    public IReadOnlyList<DadNpcDutyCatalogRow> DutyCatalog() => [new(4, "Synthetic duty", 1036, 3, 44, 1), new(6, "Synthetic second leveling duty", 1037, 3, 45, 16)];
    public IReadOnlyList<DadDawnCatalogRow> DawnCatalog() => [new(1, 4, true, 4), new(2, 6, false, 4)];
    public bool AgentAvailable(DadNpcDutyQueueMode mode) => true;
    public bool AddonReady(DadNpcDutyQueueMode mode) => open.Contains(mode);
    public bool MainCommandEnabled(DadNpcDutyQueueMode mode) => true;
    public uint ExpansionCount(DadNpcDutyQueueMode mode) => 6;
    public uint SelectedContentId(DadNpcDutyQueueMode mode) => selected.GetValueOrDefault(mode);
    public void Open(DadNpcDutyQueueMode mode, uint contentId)
    {
        if (contentId != 4 && (mode != DadNpcDutyQueueMode.DutySupport || contentId is not (44 or 45 or 6)))
            throw unexpected($"npc-open:{mode}:{contentId}");
        open.Add(mode);
        selected[mode] = contentId is 44 or 45 ? 0u : mode == DadNpcDutyQueueMode.Trust ? 1u : contentId;
        observe($"native:npc-open:{mode}:{contentId}");
    }
    public void Register(DadNpcDutyQueueMode mode)
    {
        if (!open.Contains(mode) || SelectedContentId(mode) == 0 || (mode == DadNpcDutyQueueMode.Trust && party.Count != 3))
            throw unexpected($"npc-register-without-selection:{mode}");
        observe($"native:npc-selected:{SelectedContentId(mode)}");
        game.QueuedTerritory = SelectedContentId(mode) == 6 ? 1037u : 1036u;
        observe($"native:npc-register:{mode}"); game.Stage = "queued";
    }
    public void UpdateTrustAddon() => observe("native:trust-update");
    public void ClearTrustParty() { party.Clear(); observe("native:trust-clear"); }
    public bool TrustMembersAvailable => true;
    public int TrustMemberCount => 8;
    public DadTrustMemberObservation TrustMember(byte index) => index switch
    {
        0 => new(6, AlphinaudLevel, 40, DadTrustRoles.Healer, "Alphinaud", true),
        1 => new(1, 100, 35, DadTrustRoles.Dps, "Alisaie", true),
        2 => new(2, 100, 37, DadTrustRoles.Tank, "Thancred", true),
        3 => new(3, 100, 33, DadTrustRoles.Healer, "Urianger", true),
        4 => new(4, 100, 25, DadTrustRoles.Dps, "Y'shtola", true),
        5 => new(5, 80, 29, DadTrustRoles.Dps, "Ryne", true),
        6 => new(8, 100, 25, DadTrustRoles.All, "G'raha Tia", true),
        7 => new(103, 90, 39, DadTrustRoles.Dps, "Zero", true),
        _ => throw unexpected($"trust-member:{index}"),
    };
    public bool AddTrustMember(byte index)
    {
        if (index >= 8 || !party.Add(index)) throw unexpected($"trust-add:{index}");
        observe($"native:trust-add:{index}");
        return true;
    }
    public bool TrustPartyFull => party.Count == 3;
    public IReadOnlyList<byte> SelectedTrustMemberIndices() => party.Order().ToArray();
    public DadTrustNativeDuty? SelectedTrustDuty() => open.Contains(DadNpcDutyQueueMode.Trust)
        ? new(4, true, party.Count == 3) : null;
    public IReadOnlyList<DadTrustNativeDuty> TrustDutyUnlocks() => [new(4, true, false)];
}
