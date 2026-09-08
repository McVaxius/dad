using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace dad.Services;

internal sealed record DadNpcDutyCatalogRow(uint RowId, string Name, uint TerritoryId,
    uint? ExVersion, uint ContentId, byte LevelRequired);
internal sealed record DadDawnCatalogRow(uint RowId, uint ContentFinderId, bool Trust, int ParticipableCount);
internal readonly record struct DadTrustMemberObservation(uint MemberId, int Level);

internal interface IDadNpcDutyNativeAccess
{
    uint LocalJobId { get; }
    IReadOnlyList<DadNpcDutyCatalogRow> DutyCatalog();
    IReadOnlyList<DadDawnCatalogRow> DawnCatalog();
    bool AgentAvailable(DadNpcDutyQueueMode mode);
    bool AddonReady(DadNpcDutyQueueMode mode);
    bool MainCommandEnabled(DadNpcDutyQueueMode mode);
    uint ExpansionCount(DadNpcDutyQueueMode mode);
    uint SelectedContentId(DadNpcDutyQueueMode mode);
    void Open(DadNpcDutyQueueMode mode, uint contentId);
    void Register(DadNpcDutyQueueMode mode);
    void UpdateTrustAddon();
    void ClearTrustParty();
    bool TrustMembersAvailable { get; }
    DadTrustMemberObservation TrustMember(byte index);
    void AddTrustMember(byte index);
}

internal sealed unsafe class DadNpcDutyNativeAccess : IDadNpcDutyNativeAccess
{
    public uint LocalJobId => Plugin.ObjectTable.LocalPlayer is { } player && player.ClassJob.IsValid ? player.ClassJob.RowId : 0;
    public IReadOnlyList<DadNpcDutyCatalogRow> DutyCatalog()
        => Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().Select(row => new DadNpcDutyCatalogRow(
            row.RowId, row.Name.ToString(), row.TerritoryType.ValueNullable?.RowId ?? 0,
            row.TerritoryType.ValueNullable?.ExVersion.ValueNullable?.RowId, row.Content.RowId, row.ClassJobLevelRequired)).ToArray();
    public IReadOnlyList<DadDawnCatalogRow> DawnCatalog()
    {
        var participable = Plugin.DataManager.GetSubrowExcelSheet<DawnContentParticipable>();
        return Plugin.DataManager.GetExcelSheet<DawnContent>().Select(row => new DadDawnCatalogRow(
            row.RowId, row.Content.ValueNullable?.RowId ?? 0, row.Unknown13, participable.GetSubrowCount(row.RowId))).ToArray();
    }
    public bool AgentAvailable(DadNpcDutyQueueMode mode) => mode == DadNpcDutyQueueMode.Trust
        ? AgentDawn.Instance() != null : AgentDawnStory.Instance() != null;
    public bool AddonReady(DadNpcDutyQueueMode mode) => mode == DadNpcDutyQueueMode.Trust
        ? AgentDawn.Instance()->IsAddonReady() : AgentDawnStory.Instance()->IsAddonReady();
    public bool MainCommandEnabled(DadNpcDutyQueueMode mode) => AgentHUD.Instance() != null &&
        AgentHUD.Instance()->IsMainCommandEnabled(mode == DadNpcDutyQueueMode.Trust ? 82u : 91u);
    public uint ExpansionCount(DadNpcDutyQueueMode mode) => mode == DadNpcDutyQueueMode.Trust
        ? AgentDawn.Instance()->Data->ContentData.ExpansionCount : AgentDawnStory.Instance()->Data->ContentData.ExpansionCount;
    public uint SelectedContentId(DadNpcDutyQueueMode mode)
    {
        if (mode == DadNpcDutyQueueMode.Trust) return AgentDawn.Instance()->SelectedContentId;
        var data = &AgentDawnStory.Instance()->Data->ContentData;
        return data->ContentEntries[data->SelectedContentEntry].ContentFinderConditionId;
    }
    public void Open(DadNpcDutyQueueMode mode, uint contentId)
    {
        if (mode == DadNpcDutyQueueMode.Trust) RaptureAtkModule.Instance()->OpenDawn(contentId);
        else RaptureAtkModule.Instance()->OpenDawnStory(contentId);
    }
    public void Register(DadNpcDutyQueueMode mode)
    {
        if (mode == DadNpcDutyQueueMode.Trust) AgentDawn.Instance()->RegisterForDuty();
        else AgentDawnStory.Instance()->RegisterForDuty();
    }
    public void UpdateTrustAddon() => AgentDawn.Instance()->UpdateAddon();
    public void ClearTrustParty() => AgentDawn.Instance()->Data->PartyData.ClearParty();
    public bool TrustMembersAvailable => AgentDawn.Instance()->Data->MemberData.GetMembers(
        AgentDawn.Instance()->Data->MemberData.CurrentMembersIndex) != null;
    public DadTrustMemberObservation TrustMember(byte index)
    {
        var members = AgentDawn.Instance()->Data->MemberData.GetMembers(AgentDawn.Instance()->Data->MemberData.CurrentMembersIndex);
        if (members == null) throw new InvalidOperationException("Trust member data is unavailable.");
        var entry = members[index];
        return new(entry.MemberId, entry.Level);
    }
    public void AddTrustMember(byte index)
    {
        var agent = AgentDawn.Instance();
        var members = agent->Data->MemberData.GetMembers(agent->Data->MemberData.CurrentMembersIndex);
        if (members == null) throw new InvalidOperationException("Trust member data is unavailable.");
        var entry = members[index];
        agent->Data->PartyData.AddMember(index, &entry);
    }
}
