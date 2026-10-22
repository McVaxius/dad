using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Dalamud.Memory;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using dad.Models;

namespace dad.Services;

public sealed unsafe partial class DadTitleMenuReadinessService
{
    private const string LobbyErrorSignature = "40 53 48 83 EC 30 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 8B D0";
    private delegate byte LobbyErrorDelegate(nint owner, nint context, nint value);
    [Signature(LobbyErrorSignature, DetourName = nameof(HandleLobbyError), Fallibility = Fallibility.Fallible)]
    private Hook<LobbyErrorDelegate>? lobbyErrorHook = null;
    private readonly DadConnectionRecovery connectionRecovery = new();
    private readonly object recoveryGate = new();
    private Configuration? recoveryConfiguration;
    private Func<DadConnectionRecoveryIdentity?>? recoveryIdentity;
    private Func<bool>? anotherLoginOwner;
    private Func<DadConnectionRecoveryObservation>? observeRecovery;
    private Action<DadConnectionRecoveryAction, DadConnectionRecoveryIdentity, DadConnectionRecoveryObservation>? executeRecovery;
    private IDataManager? recoveryData;
    private bool recoveryLoginPromptExpected;
    private bool recoveryLogoutSubscribed;
    private string recoveryProtectionIssue = string.Empty;

    public string ConnectionRecoveryStatus => !string.IsNullOrEmpty(recoveryProtectionIssue)
        ? $"{recoveryProtectionIssue} {connectionRecovery.Status}"
        : LobbyProtectionEnabled && !ReconnectEnabled
            ? $"Lobby protection is enabled. {connectionRecovery.Status}"
            : connectionRecovery.Status;

    internal void ConfigureConnectionRecovery(Configuration configuration,
        Func<DadConnectionRecoveryIdentity?> identity, Func<bool> loginOwnedElsewhere,
        IDataManager data, IGameInteropProvider interop, IPluginLog log)
    {
        recoveryData = data;
        ConfigureConnectionRecovery(configuration, identity, loginOwnedElsewhere,
            CaptureRecoverySurface, ExecuteRecoveryAction);
        clientState.Logout += OnRecoveryLogout;
        recoveryLogoutSubscribed = true;
        try
        {
            interop.InitializeFromAttributes(this);
            if (lobbyErrorHook == null)
            {
                recoveryProtectionIssue = "Lobby protection is unavailable; check the installed game version.";
                log.Warning("[dad] Connection recovery's lobby protection signature is unavailable.");
            }
            else UpdateLobbyProtection();
        }
        catch (Exception exception)
        {
            recoveryProtectionIssue = "Lobby protection could not initialize; check the plugin log.";
            lobbyErrorHook?.Dispose();
            lobbyErrorHook = null;
            log.Warning(exception, "[dad] Connection recovery's lobby protection could not initialize.");
        }
    }

    // The virtual lab replaces only observations and native callbacks, retaining this owner.
    internal void ConfigureConnectionRecovery(Configuration configuration,
        Func<DadConnectionRecoveryIdentity?> identity, Func<bool> loginOwnedElsewhere,
        Func<DadConnectionRecoveryObservation> observe,
        Action<DadConnectionRecoveryAction, DadConnectionRecoveryIdentity, DadConnectionRecoveryObservation> execute)
    {
        recoveryConfiguration = configuration;
        recoveryIdentity = identity;
        anotherLoginOwner = loginOwnedElsewhere;
        observeRecovery = observe;
        executeRecovery = execute;
    }

    public void CancelConnectionRecovery(string reason)
    {
        lock (recoveryGate)
        {
            connectionRecovery.Cancel(reason, forgetSession: true);
            recoveryLoginPromptExpected = false;
        }
    }

    internal void OnRecoveryLogout(int _, int code)
    {
        lock (recoveryGate)
        {
            recoveryLoginPromptExpected = false;
            if (ReconnectEnabled) connectionRecovery.ObserveLogout(code);
            else CancelConnectionRecovery("Connection recovery is off.");
        }
    }

    private bool LobbyProtectionEnabled => !disposed && recoveryConfiguration is
        { ConnectionRecoveryEnabled: true };

    private bool ReconnectEnabled => LobbyProtectionEnabled && recoveryConfiguration is { PluginEnabled: true };

    private byte HandleLobbyError(nint owner, nint context, nint value)
    {
        if (value != 0) ProtectConnectionError(new Span<byte>((void*)value, 16));
        return lobbyErrorHook!.Original(owner, context, value);
    }

    internal bool ProtectConnectionError(Span<byte> value)
    {
        var protectedError = DadConnectionRecovery.ProtectLobbyError(value, LobbyProtectionEnabled, out var code);
        if (protectedError && ReconnectEnabled) connectionRecovery.ObserveError(code);
        return protectedError;
    }

    private void UpdateConnectionRecovery()
    {
        lock (recoveryGate) UpdateOwnedConnectionRecovery();
    }

    private void UpdateOwnedConnectionRecovery()
    {
        if (recoveryConfiguration == null) return;
        UpdateLobbyProtection();
        var enabled = ReconnectEnabled;
        var identity = enabled ? recoveryIdentity!() : null;
        var checkingLogin = enabled && identity == null && connectionRecovery.RequiresLoginOwnershipCheck;
        var observation = checkingLogin ? observeRecovery!() : default;
        var action = connectionRecovery.Update(enabled, recoveryConfiguration.ClientAccountId,
            identity, enabled && keyState[0x10],
            checkingLogin && anotherLoginOwner!(), observation, DadClock.UtcNow);
        if (!connectionRecovery.IsActive) recoveryLoginPromptExpected = false;
        if (action == DadConnectionRecoveryAction.None || connectionRecovery.Target is not { } target) return;

        // Recheck both ownership and the exact native surface at the callback boundary.
        if (connectionRecovery.Target != target || !ReconnectEnabled || keyState[0x10] ||
            anotherLoginOwner!() || observeRecovery!() != observation)
        {
            CancelConnectionRecovery("Connection recovery lost its exact action boundary.");
            return;
        }
        executeRecovery!(action, target, observation);
    }

    private void UpdateLobbyProtection()
    {
        if (lobbyErrorHook == null || lobbyErrorHook.IsEnabled == LobbyProtectionEnabled) return;
        if (LobbyProtectionEnabled) lobbyErrorHook.Enable();
        else lobbyErrorHook.Disable();
    }

    private DadConnectionRecoveryObservation CaptureRecoverySurface()
    {
        if (clientState.IsLoggedIn) return new(DadConnectionRecoverySurface.World, false);
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return default;
        AtkUnitBase* Ready(string name)
        {
            var addon = manager->GetAddonByName(name);
            return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
        }
        DadConnectionRecoveryObservation Surface(DadConnectionRecoverySurface kind, AtkUnitBase* addon, bool ready = true)
            => new(kind, ready, (nint)addon);
        var dialogue = Ready("Dialogue");
        if (dialogue != null)
            return Surface(DadConnectionRecoverySurface.Dialogue, dialogue,
                IsRecoveryButtonUsable(dialogue->GetComponentButtonById(4)));
        foreach (var name in new[] { "_TitleError", "TitleError", "TitleServerError", "TitleNetworkError" })
        {
            var error = Ready(name);
            if (error != null) return Surface(DadConnectionRecoverySurface.LobbyError, error, false);
        }
        var yesno = Ready("SelectYesno");
        if (yesno != null)
        {
            var prompt = (AddonSelectYesno*)yesno;
            var text = prompt->PromptText == null ? string.Empty :
                MemoryHelper.ReadSeStringNullTerminated((nint)prompt->PromptText->NodeText.StringPtr.Value).TextValue;
            return Surface(DadConnectionRecoverySurface.LoginConfirmation, yesno,
                recoveryLoginPromptExpected && IsRecoveryCharacterSelected() &&
                IsRecoveryButtonUsable(prompt->YesButton) && IsRecoveryLoginConfirmation(text));
        }
        var select = Ready("SelectString");
        if (select != null) return Surface(DadConnectionRecoverySurface.ServiceAccount, select);
        if (Ready("SelectOk") != null) return new(DadConnectionRecoverySurface.OtherDialog, false);
        var connecting = Ready("TitleConnect");
        if (connecting != null) return Surface(DadConnectionRecoverySurface.Connecting, connecting, false);
        var worlds = Ready("_CharaSelectWorldServer");
        if (worlds != null) return Surface(DadConnectionRecoverySurface.WorldSelect, worlds);
        var characters = Ready("_CharaSelectListMenu");
        if (characters != null) return Surface(DadConnectionRecoverySurface.CharacterSelect, characters);
        var dc = Ready("TitleDCWorldMap");
        if (dc != null) return Surface(DadConnectionRecoverySurface.DataCenter, dc);
        var title = Ready("_TitleMenu");
        if (title != null) return Surface(DadConnectionRecoverySurface.Title, title,
            title->UldManager.NodeList != null && title->UldManager.NodeListCount > 7 &&
            title->UldManager.NodeList[3] != null && title->UldManager.NodeList[3]->Color.A == byte.MaxValue &&
            title->UldManager.NodeList[7] != null && title->UldManager.NodeList[7]->IsVisible() &&
            IsRecoveryButtonUsable(title->GetComponentButtonById(5)) && !HasAnyActiveCondition());
        return default;
    }

    internal static bool IsRecoveryLoginConfirmation(string text)
        => new[] { "Log in", "Logging in", "last logged out", "でログインします", "環境で最後にログ",
            "einloggen?", "eingeloggt", "ausgeloggt hast", "Se connecter", "connecter avec", "dernière connexion n'a pas" }
            .Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));

    private void ExecuteRecoveryAction(DadConnectionRecoveryAction action,
        DadConnectionRecoveryIdentity target, DadConnectionRecoveryObservation observation)
    {
        var addon = (AtkUnitBase*)observation.AddonAddress;
        switch (action)
        {
            case DadConnectionRecoveryAction.DismissError:
                recoveryLoginPromptExpected = false;
                addon->GetComponentButtonById(4)->ClickAddonButton(addon);
                break;
            case DadConnectionRecoveryAction.OpenDataCenter:
                addon->GetComponentButtonById(5)->ClickAddonButton(addon);
                break;
            case DadConnectionRecoveryAction.SelectDataCenter:
                // The DC was captured from the observed current world, including visitors.
                var dc = recoveryData!.GetExcelSheet<WorldDCGroupType>().GetRow(target.CurrentDataCenterId);
                if (dc.Region.RowId != 0 && !string.IsNullOrWhiteSpace(dc.Name.ToString()))
                    FireRecoveryCallback(addon, 17, (int)target.CurrentDataCenterId);
                break;
            case DadConnectionRecoveryAction.SelectWorld:
                var module = RaptureAtkModule.Instance();
                if (module == null || module->AtkArrayDataHolder.StringArrays == null) return;
                var names = module->AtkArrayDataHolder.StringArrays[1];
                if (names == null || names->StringArray == null) return;
                for (var index = 0; index < Math.Min(names->Size, 16); index++)
                {
                    var text = names->StringArray[index].Value;
                    if (text == null) break;
                    var name = MemoryHelper.ReadStringNullTerminated((nint)text).Trim();
                    if (string.IsNullOrEmpty(name)) break;
                    if (name == target.CurrentWorld)
                    {
                        FireRecoveryCallback(addon, 25, 0, index);
                        break;
                    }
                }
                break;
            case DadConnectionRecoveryAction.SelectCharacter:
                var lobby = AgentLobby.Instance();
                if (lobby == null) return;
                var characters = lobby->LobbyData.CharaSelectEntries;
                for (var index = 0; index < characters.Count; index++)
                {
                    var character = characters[index].Value;
                    if (character == null || character->ContentId != target.ContentId ||
                        character->NameString != target.CharacterName ||
                        recoveryData!.GetExcelSheet<World>().GetRow(character->HomeWorldId).Name.ToString() != target.HomeWorld)
                        continue;
                    if (recoveryData!.GetExcelSheet<World>().GetRow(character->CurrentWorldId).Name.ToString() != target.CurrentWorld)
                    {
                        if (lobby->HoveredCharacterContentId != target.ContentId)
                            FireRecoveryCallback(addon, 21, index);
                        else if (IsRecoveryButtonUsable(addon->GetComponentButtonById(4)))
                            addon->GetComponentButtonById(4)->ClickAddonButton(addon);
                        return;
                    }
                    var stage = AtkStage.Instance();
                    if (stage == null || index > byte.MaxValue - 5) return;
                    var eventIndex = (byte)(5 + index);
                    var click = new AtkEvent
                    {
                        Listener = (AtkEventListener*)addon, Param = eventIndex,
                        Target = &stage->AtkEventTarget,
                        State = new AtkEventState { EventType = AtkEventType.MouseClick },
                    };
                    AtkEventData data = default;
                    recoveryLoginPromptExpected = true;
                    addon->ReceiveEvent(AtkEventType.MouseClick, eventIndex, &click, &data);
                    return;
                }
                break;
            case DadConnectionRecoveryAction.ConfirmLogin:
                var prompt = (AddonSelectYesno*)addon;
                if (recoveryLoginPromptExpected && IsRecoveryCharacterSelected() && IsRecoveryButtonUsable(prompt->YesButton))
                {
                    prompt->YesButton->ClickAddonButton(addon);
                    recoveryLoginPromptExpected = false;
                }
                break;
        }
    }

    private static bool IsRecoveryButtonUsable(AtkComponentButton* button)
        => button != null && button->IsEnabled && button->AtkComponentBase.OwnerNode != null &&
           button->AtkComponentBase.OwnerNode->AtkResNode.AtkEventManager.Event != null;

    private bool IsRecoveryCharacterSelected()
        => connectionRecovery.Target is { } target && AgentLobby.Instance() != null &&
           AgentLobby.Instance()->HoveredCharacterContentId == target.ContentId;

    private static void FireRecoveryCallback(AtkUnitBase* addon, params int[] arguments)
    {
        var values = stackalloc AtkValue[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
            values[index] = new AtkValue { Type = AtkValueType.Int, Int = arguments[index] };
        addon->FireCallback((uint)arguments.Length, values, true);
    }

    private void DisposeConnectionRecovery()
    {
        CancelConnectionRecovery("Connection recovery disposed.");
        if (recoveryLogoutSubscribed) clientState.Logout -= OnRecoveryLogout;
        lobbyErrorHook?.Dispose();
        lobbyErrorHook = null;
    }
}
