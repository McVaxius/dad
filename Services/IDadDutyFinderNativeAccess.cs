using Dalamud.Game.ClientState.Conditions;
using dad.Models;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace dad.Services;

internal readonly record struct DadQueueAddonObservation(bool Visible, bool Ready);
internal sealed record DadRegularDutyCatalogRow(uint RowId, string Name, bool IsInDutyFinder,
    uint TerritoryId, bool AllowUndersized, bool HighEndDuty, int QueueSize, bool PvP = false);

// Native observations and mutations only. Queue ownership, mapping proof, admission,
// throttles and restoration remain in DadLocalDutyQueueService.
internal interface IDadDutyFinderNativeAccess
{
    event Action<uint> DutyCompleted;
    bool IsLoggedIn { get; }
    bool HasLocalPlayer { get; }
    uint TerritoryType { get; }
    ulong ContentId { get; }
    bool Condition(ConditionFlag flag);
    bool ContentsFinderAvailable { get; }
    bool AgentAvailable { get; }
    bool QueueStateActive { get; }
    bool IsUnrestrictedParty { get; set; }
    bool MainCommandEnabled { get; }
    DadQueueAddonObservation Addon(string name);
    DadDutyFinderLiveContentType SelectedType { get; }
    uint SelectedId { get; }
    int InterfaceSelectedId { get; }
    bool HasRouletteSelected { get; }
    void OpenRegularDuty(uint id);
    void OpenRouletteDuty(byte id);
    void Show();
    void Callback(string addon, params int[] values);
    bool TryCapture(out DadDutyFinderListSnapshot snapshot, out string reason);
    IReadOnlyList<DadRegularDutyCatalogRow> DutyCatalog();
    IReadOnlyList<DadPlannerRouletteOption> RouletteCatalog();
}

internal sealed unsafe class DadDutyFinderNativeAccess : IDadDutyFinderNativeAccess
{
    private readonly Dictionary<Action<uint>, Dalamud.Plugin.Services.IDutyState.DutyCompletedDelegate> completionHandlers = new();
    public event Action<uint> DutyCompleted
    {
        add
        {
            Dalamud.Plugin.Services.IDutyState.DutyCompletedDelegate handler = args => value(args.TerritoryType.RowId);
            Plugin.DutyState.DutyCompleted += handler;
            completionHandlers.Add(value, handler);
        }
        remove
        {
            if (completionHandlers.Remove(value, out var handler)) Plugin.DutyState.DutyCompleted -= handler;
        }
    }
    public bool IsLoggedIn => Plugin.ClientState.IsLoggedIn;
    public bool HasLocalPlayer => Plugin.ObjectTable.LocalPlayer != null;
    public uint TerritoryType => Plugin.ClientState.TerritoryType;
    public ulong ContentId => Plugin.PlayerState.ContentId;
    public bool Condition(ConditionFlag flag) => Plugin.Condition[flag];
    public bool ContentsFinderAvailable => ContentsFinder.Instance() != null;
    public bool AgentAvailable => AgentContentsFinder.Instance() != null;
    public bool QueueStateActive
    {
        get
        {
            var finder = ContentsFinder.Instance();
            return finder != null && finder->QueueInfo.QueueState is ContentsFinderQueueState.Pending or
                ContentsFinderQueueState.Queued or ContentsFinderQueueState.Ready or ContentsFinderQueueState.Accepted;
        }
    }
    public bool IsUnrestrictedParty
    {
        get => ContentsFinder.Instance()->IsUnrestrictedParty;
        set => ContentsFinder.Instance()->IsUnrestrictedParty = value;
    }
    public bool MainCommandEnabled => AgentHUD.Instance() != null && AgentHUD.Instance()->IsMainCommandEnabled(33);
    public DadQueueAddonObservation Addon(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return new(addon != null && addon->IsVisible, addon != null && addon->IsReady);
    }
    public DadDutyFinderLiveContentType SelectedType => AgentContentsFinder.Instance()->SelectedDuty.ContentType switch
    {
        ContentsType.Regular => DadDutyFinderLiveContentType.Regular,
        ContentsType.Roulette => DadDutyFinderLiveContentType.Roulette,
        _ => DadDutyFinderLiveContentType.None,
    };
    public uint SelectedId => AgentContentsFinder.Instance()->SelectedDuty.Id;
    public int InterfaceSelectedId => AgentContentsFinder.Instance()->InterfaceSub.SelectedDutyId;
    public bool HasRouletteSelected => AgentContentsFinder.Instance()->HasRouletteSelected;
    public void OpenRegularDuty(uint id) => AgentContentsFinder.Instance()->OpenRegularDuty(id);
    public void OpenRouletteDuty(byte id) => AgentContentsFinder.Instance()->OpenRouletteDuty(id);
    public void Show() => AgentContentsFinder.Instance()->Show();
    public void Callback(string name, params int[] values)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            throw new InvalidOperationException($"{name} is unavailable for a callback.");
        var native = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            native[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
            native[i].Int = values[i];
        }
        addon->FireCallback((uint)values.Length, native, true);
    }
    public bool TryCapture(out DadDutyFinderListSnapshot snapshot, out string reason)
        => DadDutyFinderLiveEntryScanner.TryCapture(AgentContentsFinder.Instance(),
            RaptureAtkUnitManager.Instance()->GetAddonByName("ContentsFinder"), out snapshot, out reason);
    public IReadOnlyList<DadRegularDutyCatalogRow> DutyCatalog()
        => Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().Select(row => new DadRegularDutyCatalogRow(
            row.RowId, row.Name.ToString().Trim(), row.IsInDutyFinder, row.TerritoryType.ValueNullable?.RowId ?? 0,
            row.AllowUndersized, row.HighEndDuty, Math.Max(1, row.QueueMaxPlayers > 0
                ? (int)row.QueueMaxPlayers : row.ContentMemberType.ValueNullable?.MembersPerParty ?? 1), row.PvP)).ToArray();
    public IReadOnlyList<DadPlannerRouletteOption> RouletteCatalog()
        => new DadRouletteCatalogService(Plugin.DataManager).GetOptions();
}
