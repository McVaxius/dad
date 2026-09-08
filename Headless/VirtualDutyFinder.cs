using Dalamud.Game.ClientState.Conditions;
using dad.Models;
using dad.Services;

namespace dad.Headless;

internal sealed class VirtualDutyFinder(Func<ulong> contentId, Func<bool> loggedIn, Action<string> observe,
    Func<string, Exception> unexpected) : IDadDutyFinderNativeAccess
{
    private string stage = "outside";
    private bool unrestricted;
    private bool open;
    private DadDutyFinderLiveContentType listType;
    private uint listId;
    private uint selectedId;
    private string fault = "none";
    public string Fault
    {
        get => fault;
        set => fault = value is "none" or "unstable-list" or "wrong-selection" or "stale-character" or "unavailable"
            ? value : throw unexpected($"duty-fault:{value}");
    }
    public event Action<uint>? DutyCompleted;
    public string Stage
    {
        get => stage;
        set
        {
            if (value is not ("outside" or "queued" or "confirm" or "duty" or "completed"))
                throw unexpected($"duty-stage:{value}");
            var completed = value == "completed" && stage != value;
            stage = value;
            if (completed) DutyCompleted?.Invoke(TerritoryType);
        }
    }
    public bool IsLoggedIn => loggedIn();
    public bool HasLocalPlayer => IsLoggedIn;
    public uint TerritoryType => Stage is "duty" or "completed" ? 1036u : 1u;
    public ulong ContentId => contentId();
    public bool Condition(ConditionFlag flag) => flag switch
    {
        ConditionFlag.BoundByDuty or ConditionFlag.BoundByDuty56 => Stage is "duty" or "completed",
        ConditionFlag.InDutyQueue or ConditionFlag.WaitingForDuty or ConditionFlag.WaitingForDutyFinder => Stage is "queued" or "confirm",
        ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51 => false,
        _ when Enum.IsDefined(flag) => false,
        _ => throw unexpected($"duty-condition:{flag}"),
    };
    public bool ContentsFinderAvailable => Fault != "unavailable";
    public bool AgentAvailable => true;
    public bool QueueStateActive => Stage is "queued" or "confirm";
    public bool IsUnrestrictedParty
    {
        get => ContentsFinderAvailable ? unrestricted : throw new InvalidOperationException("Synthetic ContentsFinder is unavailable.");
        set
        {
            if (!ContentsFinderAvailable) throw unexpected("unrestricted-write-without-contents-finder");
            unrestricted = value; observe($"native:unrestricted:{value}");
        }
    }
    public bool ObservedUnrestrictedParty => unrestricted;
    public bool MainCommandEnabled => true;
    public DadQueueAddonObservation Addon(string name) => name switch
    {
        "ContentsFinder" => new(open, open),
        "ContentsFinderConfirm" => new(Stage == "confirm", Stage == "confirm"),
        _ => throw unexpected($"duty-addon:{name}"),
    };
    public DadDutyFinderLiveContentType SelectedType { get; private set; }
    public uint SelectedId { get => Fault == "wrong-selection" && selectedId != 0 ? 99u : selectedId; private set => selectedId = value; }
    public int InterfaceSelectedId { get; private set; }
    public bool HasRouletteSelected => SelectedType == DadDutyFinderLiveContentType.Roulette;
    public void OpenRegularDuty(uint id)
    {
        if (id != 4) throw unexpected($"open-duty:{id}");
        open = true; listType = DadDutyFinderLiveContentType.Regular; listId = id;
        observe($"native:open-duty:{id}");
    }
    public void OpenRouletteDuty(byte id)
    {
        if (id != 9) throw unexpected($"open-roulette:{id}");
        open = true; listType = DadDutyFinderLiveContentType.Roulette; listId = id;
        observe($"native:open-roulette:{id}");
    }
    public void Show() { open = true; observe("native:show-duty-finder"); }
    public void Callback(string addon, params int[] values)
    {
        if (!Addon(addon).Ready) throw unexpected($"hidden-addon-callback:{addon}");
        var command = string.Join(",", values);
        observe($"native:callback:{addon}:{command}");
        switch ((addon, command))
        {
            case ("ContentsFinder", "12,1"):
                SelectedType = DadDutyFinderLiveContentType.None; SelectedId = 0; InterfaceSelectedId = 0; break;
            case ("ContentsFinder", "3,1") when listId != 0:
                SelectedType = listType; SelectedId = listId; InterfaceSelectedId = (int)listId; break;
            case ("ContentsFinder", "12,0") when SelectedId != 0:
                Stage = "queued"; break;
            case ("ContentsFinderConfirm", "8"):
                Stage = "queued"; break;
            default: throw unexpected($"duty-callback:{addon}:{command}");
        }
    }
    public bool TryCapture(out DadDutyFinderListSnapshot snapshot, out string reason)
    {
        snapshot = new()
        {
            CharacterContentId = Fault == "stale-character" ? 9999u : ContentId, AddonIdentity = 1, AgentIdentity = 2, DutyListIdentity = 3,
            ContentListStorageIdentity = 4, DutyListStorageIdentity = 5,
            DeclaredEntryCount = listId == 0 ? 0u : 1u, TreeItemCount = listId == 0 ? 0 : 1,
            ContentEntries = listId == 0 ? [] : [new(0, listType, listId, "Synthetic duty")],
            TreeItems = listId == 0 ? [] : [new(0, true, true, "Synthetic duty")],
            ListChanged = Fault == "unstable-list",
        };
        reason = open ? "" : "Synthetic Duty Finder is closed.";
        return open;
    }
    public IReadOnlyList<DadRegularDutyCatalogRow> DutyCatalog() => [new(4, "Synthetic duty", true, 1036, true, false, 4)];
    public IReadOnlyList<DadPlannerRouletteOption> RouletteCatalog() =>
        [new() { RouletteId = 9, Key = "MainScenario", DisplayName = "Synthetic roulette", IsAvailable = true }];
}
