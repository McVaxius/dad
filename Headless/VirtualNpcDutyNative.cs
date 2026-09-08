using dad.Services;

namespace dad.Headless;

internal sealed class VirtualNpcDutyNative(VirtualDutyFinder game, Func<uint> jobId, Action<string> observe,
    Func<string, Exception> unexpected) : IDadNpcDutyNativeAccess
{
    private readonly HashSet<DadNpcDutyQueueMode> open = [];
    private readonly Dictionary<DadNpcDutyQueueMode, uint> selected = [];
    private readonly HashSet<byte> party = [];
    public uint LocalJobId => game.HasLocalPlayer ? jobId() : 0;
    public IReadOnlyList<DadNpcDutyCatalogRow> DutyCatalog() => [new(4, "Synthetic duty", 1036, 3, 44, 1)];
    public IReadOnlyList<DadDawnCatalogRow> DawnCatalog() => [new(1, 4, true, 4)];
    public bool AgentAvailable(DadNpcDutyQueueMode mode) => true;
    public bool AddonReady(DadNpcDutyQueueMode mode) => open.Contains(mode);
    public bool MainCommandEnabled(DadNpcDutyQueueMode mode) => true;
    public uint ExpansionCount(DadNpcDutyQueueMode mode) => 6;
    public uint SelectedContentId(DadNpcDutyQueueMode mode) => selected.GetValueOrDefault(mode);
    public void Open(DadNpcDutyQueueMode mode, uint contentId)
    {
        if (contentId != 4 && (mode != DadNpcDutyQueueMode.DutySupport || contentId != 44))
            throw unexpected($"npc-open:{mode}:{contentId}");
        open.Add(mode);
        selected[mode] = contentId == 44 ? 0u : mode == DadNpcDutyQueueMode.Trust ? 1u : 4u;
        observe($"native:npc-open:{mode}:{contentId}");
    }
    public void Register(DadNpcDutyQueueMode mode)
    {
        if (!open.Contains(mode) || SelectedContentId(mode) == 0 || (mode == DadNpcDutyQueueMode.Trust && party.Count != 3))
            throw unexpected($"npc-register-without-selection:{mode}");
        observe($"native:npc-register:{mode}"); game.Stage = "queued";
    }
    public void UpdateTrustAddon() => observe("native:trust-update");
    public void ClearTrustParty() { party.Clear(); observe("native:trust-clear"); }
    public bool TrustMembersAvailable => true;
    public DadTrustMemberObservation TrustMember(byte index) => index < 8 ? new((uint)index + 1, 100)
        : throw unexpected($"trust-member:{index}");
    public void AddTrustMember(byte index)
    {
        if (index >= 8 || !party.Add(index)) throw unexpected($"trust-add:{index}");
        observe($"native:trust-add:{index}");
    }
}
