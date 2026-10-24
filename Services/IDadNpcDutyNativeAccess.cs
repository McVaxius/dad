using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using dad.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace dad.Services;

internal sealed record DadNpcDutyCatalogRow(uint RowId, string Name, uint TerritoryId,
    uint? ExVersion, uint ContentId, byte LevelRequired);
internal sealed record DadDawnCatalogRow(uint RowId, uint ContentFinderId, bool Trust, int ParticipableCount);
internal readonly record struct DadTrustMemberObservation(uint MemberId, int Level, uint ClassJobId,
    DadTrustRoles Roles, string Name, bool Initialized, bool Unlocked = true);
internal sealed record DadTrustNativeDuty(uint ContentFinderConditionId, bool Unlocked, bool AvailableToCurrentParty);

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
    int TrustMemberCount { get; }
    DadTrustMemberObservation TrustMember(byte index);
    bool AddTrustMember(byte index);
    bool TrustPartyFull { get; }
    IReadOnlyList<byte> SelectedTrustMemberIndices();
    DadTrustNativeDuty? SelectedTrustDuty();
    IReadOnlyList<DadTrustNativeDuty> TrustDutyUnlocks();
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
        ? AgentDawn.Instance() != null && AgentDawn.Instance()->Data != null
        : AgentDawnStory.Instance() != null && AgentDawnStory.Instance()->Data != null;
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
    public bool TrustMembersAvailable => AgentAvailable(DadNpcDutyQueueMode.Trust) &&
        TrustMemberCount is > 0 and <= 12 && AgentDawn.Instance()->Data->MemberData.GetMembers(
            AgentDawn.Instance()->Data->MemberData.CurrentMembersIndex) != null;
    public int TrustMemberCount
    {
        get
        {
            if (!AgentAvailable(DadNpcDutyQueueMode.Trust)) return 0;
            var data = &AgentDawn.Instance()->Data->MemberData;
            return data->CurrentMembersIndex >= data->MemberCounts.Length ? 0 : data->GetMemberCount(data->CurrentMembersIndex);
        }
    }
    public DadTrustMemberObservation TrustMember(byte index)
    {
        if (!TrustMembersAvailable || index >= TrustMemberCount)
            throw new InvalidOperationException("Trust member index is unavailable.");
        var members = AgentDawn.Instance()->Data->MemberData.GetMembers(AgentDawn.Instance()->Data->MemberData.CurrentMembersIndex);
        if (members == null) throw new InvalidOperationException("Trust member data is unavailable.");
        var entry = members[index];
        var known = DadTrustNpcCatalog.TryResolve(entry.MemberId, entry.ClassJob, out var npc);
        var quest = known ? DadTrustNpcCatalog.UnlockQuest(npc) : 0;
        return new(entry.MemberId, entry.Level, entry.ClassJob, (DadTrustRoles)entry.Roles,
            entry.Name.ToString(), (entry.Flags & 1) != 0, known && (quest == 0 || QuestManager.IsQuestComplete(quest)));
    }
    public bool AddTrustMember(byte index)
    {
        if (!TrustMembersAvailable || index >= TrustMemberCount) return false;
        var agent = AgentDawn.Instance();
        var members = agent->Data->MemberData.GetMembers(agent->Data->MemberData.CurrentMembersIndex);
        if (members == null) throw new InvalidOperationException("Trust member data is unavailable.");
        var entry = members[index];
        return agent->Data->PartyData.CanAddMember(index, &entry) && agent->Data->PartyData.AddMember(index, &entry);
    }
    public bool TrustPartyFull => AgentAvailable(DadNpcDutyQueueMode.Trust) && AgentDawn.Instance()->Data->PartyData.IsFull();
    public IReadOnlyList<byte> SelectedTrustMemberIndices()
    {
        if (!AgentAvailable(DadNpcDutyQueueMode.Trust)) return [];
        var party = &AgentDawn.Instance()->Data->PartyData;
        if (party->MemberCount > party->PartyMembers.Length) return [];
        var indices = new List<byte>();
        for (var i = 0; i < party->MemberCount; i++) indices.Add(party->PartyMembers[i].MemberIndex);
        return indices;
    }

    public DadTrustNativeDuty? SelectedTrustDuty()
    {
        if (!AgentAvailable(DadNpcDutyQueueMode.Trust) || !AddonReady(DadNpcDutyQueueMode.Trust)) return null;
        var data = &AgentDawn.Instance()->Data->ContentData;
        if (data->ContentEntryCount > data->ContentEntries.Length || data->SelectedContentEntry >= data->ContentEntryCount || UIState.Instance() == null)
            return null;
        var entry = data->ContentEntries[data->SelectedContentEntry];
        return ReadTrustDuty(entry.ContentFinderConditionId, entry.DawnContentId, entry.IsTrustAvailable && !entry.IsNotYetSupported);
    }

    public IReadOnlyList<DadTrustNativeDuty> TrustDutyUnlocks()
        => Plugin.DataManager.GetExcelSheet<DawnContent>()
            .Where(static row => row.Unknown13 && row.Content.RowId != 0)
            .Select(row => ReadTrustDuty(row.Content.RowId, row.RowId, false))
            .Where(static duty => duty != null).Select(static duty => duty!).ToArray();

    private static DadTrustNativeDuty? ReadTrustDuty(uint cfc, uint dawnId, bool available)
    {
        if (UIState.Instance() == null) return null;
        var dawn = Plugin.DataManager.GetExcelSheet<DawnContent>().GetRowOrDefault(dawnId);
        var duty = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfc);
        if (dawn == null || !dawn.Value.Unknown13 || dawn.Value.Content.RowId != cfc ||
            duty == null || duty.Value.Content.RowId == 0) return null;
        // AutoDuty c62f8a1e CanTrustRun uses completion or this Dawn unlock quest.
        // This character unlock proof is independent of the currently selected job's level.
        var unlocked = UIState.IsInstanceContentUnlocked(duty.Value.Content.RowId) &&
            (UIState.IsInstanceContentCompleted(duty.Value.Content.RowId) ||
             dawn.Value.Unknown0 != 0 && QuestManager.IsQuestComplete(dawn.Value.Unknown0));
        return new(cfc, unlocked, available);
    }
}
