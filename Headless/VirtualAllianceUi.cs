using System.Text;
using System.Text.Json;
using dad.Models;
using dad.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Headless;

internal sealed unsafe class VirtualAllianceUi(Action<string> observe) : IDadAlliancePartyFinderNativeAccess, IDadAlliancePfNativeCallbackSink
{
    private DadAlliancePfCreateSnapshot editor = new()
    {
        TargetDutyId = 92, TargetDutySheetMatches = 1, DutyListLoaded = true,
        TargetDutyDropDownMatches = 1, TargetDutyEntryEnabled = true, TargetDutyDropDownIndex = 17,
    };
    private DadAlliancePfJoinSnapshot join = new();
    private DadAllianceAssignment alliance;
    private string leaderName = "", leaderWorld = "";
    private int passcode;
    private bool directPasscode, confirmation, holdJoin;
    public IDadAlliancePfNativeCallbackSink CallbackSink => this;
    public void Observe(JsonElement input)
    {
        if (input.TryGetProperty("leaderName", out var name)) leaderName = name.GetString()!;
        if (input.TryGetProperty("leaderWorld", out var world)) leaderWorld = world.GetString()!;
        if (input.TryGetProperty("passcode", out var code)) passcode = code.GetInt32();
        if (input.TryGetProperty("directPasscode", out var route)) directPasscode = route.GetBoolean();
        if (input.TryGetProperty("holdJoin", out var hold)) holdJoin = hold.GetBoolean();
        if (input.TryGetProperty("alliance", out var group)) alliance = (DadAllianceAssignment)group.GetInt32();
    }
    public DadAlliancePfEnvironment ReadEnvironment(ulong contentId) => new(true, editor.ActiveRecruitment,
        editor.ConditionVisible, editor.StoredPasscode, editor.NumberOfGroups,
        editor.SelectedDutyId == 92 ? "The Labyrinth of the Ancients" : "", alliance != DadAllianceAssignment.None, alliance);
    public DadAlliancePfCreateSnapshot Read(int requestedPasscode) => editor;
    public DadAlliancePfCreateActionResult Perform(DadAlliancePfCreateAction action, int requestedPasscode)
    {
        observe($"native:alliance-editor:{action}");
        editor = action switch
        {
            DadAlliancePfCreateAction.CloseStaleWindows => editor with { MainVisible = false, ConditionVisible = false },
            DadAlliancePfCreateAction.OpenMainWindow or DadAlliancePfCreateAction.ReloadMainWindow => editor with
                { MainVisible = true, MainReady = true, MainRecruitUsable = true },
            DadAlliancePfCreateAction.OpenConditions or DadAlliancePfCreateAction.ReloadOpenConditions => editor with
                { ConditionVisible = true, ConditionReady = true },
            DadAlliancePfCreateAction.SelectAlliance or DadAlliancePfCreateAction.ReloadRestoreAllianceTab => editor with
                { GroupTypeTab = 1, AllianceSelected = true },
            DadAlliancePfCreateAction.ReloadCloseConditions => editor with { ConditionVisible = false, ConditionReady = false },
            DadAlliancePfCreateAction.SelectRaids => editor with { SelectedCategory = 1u << 5 },
            DadAlliancePfCreateAction.SelectDuty => editor with { SelectedDutyId = 92, SelectedDutyDropDownIndex = 17, AllianceASelected = true },
            DadAlliancePfCreateAction.ApplyPreset => editor with
            {
                PrivateRecruitment = true, StoredPrivateRecruitment = true, Passcode = requestedPasscode, StoredPasscode = requestedPasscode,
                CrossWorldRecruitment = true, StoredCrossWorldRecruitment = true, EmptyComment = true, StoredEmptyComment = true,
                UnrestrictedJobs = true, StoredOpenSlotsUnrestricted = true, StoredStaleMembersCleared = true,
                NumberOfGroups = 3, SlotsPerGroup = 8, StoredSettingsExactBeforeSubmit = true, StoredSettingsExact = true,
            },
            DadAlliancePfCreateAction.Submit => editor with { ActiveRecruitment = true, ParticipatingInCrossWorldPartyOrAlliance = true,
                OwnerHandle = 9001, ConditionVisible = false, ConditionReady = false },
            _ => throw new InvalidOperationException($"Unknown alliance editor action {action}."),
        };
        return new(true, "Synthetic UI response; a later observation must acknowledge it.");
    }
    public DadAlliancePfCleanupSnapshot ReadCleanup() => new()
    {
        ActiveRecruitment = editor.ActiveRecruitment, OwnerHandle = editor.OwnerHandle,
        MainVisible = editor.MainVisible, MainReady = editor.MainReady, DetailsControlUsable = true,
        DetailVisible = join.DetailVisible, DetailReady = join.DetailReady,
        ConfirmationVisible = confirmation, ConfirmationReady = confirmation,
        ConfirmationIdentity = confirmation ? "synthetic-recruitment-end" : "", ConfirmationText = confirmation ? "End recruitment?" : "",
    };
    public DadAlliancePfCreateActionResult PerformCleanup(DadAlliancePfNativeAction action)
    {
        observe($"native:alliance-cleanup:{action}");
        switch (action)
        {
            case DadAlliancePfNativeAction.ShowOwnedRecruitment: editor = editor with { MainVisible = true, MainReady = true }; break;
            case DadAlliancePfNativeAction.OpenOwnedDetails: join = join with { DetailVisible = true, DetailReady = true }; break;
            case DadAlliancePfNativeAction.EndRecruitment: confirmation = true; break;
            case DadAlliancePfNativeAction.ConfirmEndRecruitment:
                confirmation = false; editor = editor with { ActiveRecruitment = false }; break;
            default: throw new InvalidOperationException($"Unknown alliance cleanup action {action}.");
        }
        return new(true, "Synthetic UI response.");
    }
    public DadAlliancePfJoinSnapshot ReadJoin(DadAlliancePfJoinTarget target) => join;
    public DadAlliancePfJoinActionResult Show()
    { observe("native:alliance-show"); join = join with { MainVisible = true, MainReady = true }; return new(true, "Shown."); }
    public nint ResolveAddon(string name, DadAlliancePfJoinAction action) => name switch
    {
        "LookingForGroup" when join.MainReady => 1,
        "LookingForGroupDetail" when join.DetailReady => 2,
        "SelectYesno" when join.YesNoReady => 3,
        "LookingForGroupPrivate" when join.PrivatePromptReady => 4,
        "LookingForGroup" or "LookingForGroupDetail" or "SelectYesno" or "LookingForGroupPrivate" => 0,
        _ => throw new InvalidOperationException($"Unknown alliance addon {name}."),
    };
    public void Fire(nint addonAddress, uint valueCount, AtkValue* values, bool updateState)
    {
        var payload = new object[valueCount];
        for (var i = 0; i < payload.Length; i++) payload[i] = values[i].Type switch
        {
            AtkValueType.Int => values[i].Int,
            AtkValueType.String => Encoding.UTF8.GetString(values[i].String.AsSpan()),
            _ => throw new InvalidOperationException($"Unexpected alliance value type {values[i].Type}."),
        };
        observe($"native:alliance-callback:{addonAddress}:{string.Join(',', payload)}:{updateState}");
        var number = (int)payload[0];
        switch ((int)addonAddress, number)
        {
            case (1, 20): join = join with { SearchAreaTab = checked((byte)(int)payload[1]) }; break;
            case (1, 21): join = join with { CategoryTab = checked((byte)(int)payload[1]) }; break;
            case (1, 17): join = join with { NumberOfListings = 1, MatchingListingIndexes = [0] }; break;
            case (1, 13) when (int)payload[1] == 0: break;
            case (1, 11) when (int)payload[1] == 0:
                join = join with { DetailVisible = true, DetailReady = true, DetailLeaderName = leaderName,
                    DetailLeaderWorld = leaderWorld, DetailDutyId = 92, DetailAlliance = true, DetailPrivate = true, DetailPartyCount = 3 }; break;
            case (2, -2): join = join with { DetailVisible = false, DetailReady = false }; break;
            case (2, >= 12 and <= 14):
                if (!holdJoin) join = join with { YesNoVisible = !directPasscode, YesNoReady = !directPasscode,
                    YesNoIdentity = "synthetic-join", YesNoText = $"Join {leaderName}'s party?",
                    PrivatePromptVisible = directPasscode, PrivatePromptReady = directPasscode };
                break;
            case (3, 0): join = join with { YesNoVisible = false, YesNoReady = false, PrivatePromptVisible = true, PrivatePromptReady = true }; break;
            case (4, 0) when (int)payload[1] == passcode:
                join = join with { PrivatePromptVisible = false, PrivatePromptReady = false }; break;
            default: throw new InvalidOperationException("Unexpected alliance native callback or mismatched passcode.");
        }
    }
    public (bool Visible, bool Ready, string Identity, string Text) ReadLeavePrompt() => (confirmation, confirmation, "synthetic-leave", "Leave the party?");
    public bool RequestLeave(out string error) { observe("native:alliance-leave"); confirmation = true; error = ""; return true; }
    public bool ConfirmLeave() { observe("native:alliance-confirm-leave"); confirmation = false; alliance = DadAllianceAssignment.None; return true; }
    public void ResetErrors() { }
    public void StopCreate() { observe("native:alliance-stop-editor"); editor = editor with { ConditionVisible = false, ConditionReady = false }; }
    public void Dispose() { }
}
