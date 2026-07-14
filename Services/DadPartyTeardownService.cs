using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

internal sealed unsafe class DadPartyTeardownService
{
    private readonly ICommandManager commandManager;
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private DadPartyTeardownController? controller;
    private string fallbackInviterName = string.Empty;

    public DadPartyTeardownService(
        ICommandManager commandManager,
        IPartyList partyList,
        IPlayerState playerState,
        ICondition condition,
        IPluginLog log)
    {
        this.commandManager = commandManager;
        this.partyList = partyList;
        this.playerState = playerState;
        this.condition = condition;
        this.log = log;
    }

    public void Begin(IReadOnlyCollection<ulong> expectedMembers, ulong expectedLeaderContentId, string expectedLeaderName)
    {
        var prompt = ReadPrompt();
        fallbackInviterName = expectedLeaderName?.Trim() ?? string.Empty;
        controller = new DadPartyTeardownController(
            expectedMembers,
            expectedLeaderContentId,
            DateTime.UtcNow,
            prompt.Visible,
            prompt.Identity);
    }

    public DadPartyTeardownDecision Update()
    {
        if (controller == null)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, "Party teardown controller was not initialized.");

        var prompt = ReadPrompt();
        var memberIds = partyList.Select(static member => member.ContentId).Where(static id => id != 0).ToList();
        if (memberIds.Count == 0 && playerState.ContentId != 0)
            memberIds.Add(playerState.ContentId);

        var leaderContentId = 0UL;
        var leaderIndex = partyList.PartyLeaderIndex;
        if (leaderIndex < partyList.Length)
            leaderContentId = partyList[(int)leaderIndex]?.ContentId ?? 0;

        var proxy = InfoProxyPartyInvite.Instance();
        var inviterName = proxy == null ? string.Empty : proxy->InviterName.ToString();
        if (string.IsNullOrWhiteSpace(inviterName))
            inviterName = fallbackInviterName;

        var decision = controller.Pulse(new DadPartyTeardownObservation(
            DateTime.UtcNow,
            playerState.ContentId,
            leaderContentId,
            memberIds,
            condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56],
            condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDuty] || condition[ConditionFlag.WaitingForDutyFinder],
            IsWorldStable(),
            prompt.Visible,
            prompt.Identity,
            prompt.Text,
            inviterName));

        try
        {
            if (decision.Action == DadPartyTeardownAction.SendBreakup)
                commandManager.ProcessCommand("/partycmd breakup");
            else if (decision.Action == DadPartyTeardownAction.ApprovePrompt)
                FireYes(prompt.Addon);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Party teardown mutation failed.");
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, $"Party teardown mutation failed: {ex.Message}");
        }

        return decision;
    }

    private bool IsWorldStable()
        => playerState.ContentId != 0 &&
           !condition[ConditionFlag.BetweenAreas] &&
           !condition[ConditionFlag.BetweenAreas51] &&
           !condition[ConditionFlag.Occupied] &&
           !condition[ConditionFlag.Occupied30] &&
           !condition[ConditionFlag.Occupied33] &&
           !condition[ConditionFlag.Occupied38] &&
           !condition[ConditionFlag.Occupied39] &&
           !condition[ConditionFlag.OccupiedInCutSceneEvent] &&
           !condition[ConditionFlag.OccupiedInEvent] &&
           !condition[ConditionFlag.OccupiedInQuestEvent] &&
           !condition[ConditionFlag.WatchingCutscene] &&
           !condition[ConditionFlag.InCombat] &&
           !condition[ConditionFlag.Casting] &&
           !condition[ConditionFlag.TradeOpen];

    public void Reset()
    {
        controller = null;
        fallbackInviterName = string.Empty;
    }

    private static PromptSnapshot ReadPrompt()
    {
        var addonBase = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectYesno");
        if (addonBase == null || !addonBase->IsVisible)
            return default;

        var addon = (AddonSelectYesno*)addonBase;
        var text = addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString().Trim();
        return new PromptSnapshot(true, $"{(nint)addonBase:X}:{text}", text, addonBase);
    }

    private static void FireYes(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible)
            return;

        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        addon->FireCallback(1, values, true);
    }

    private readonly struct PromptSnapshot
    {
        public PromptSnapshot(bool visible, string identity, string text, AtkUnitBase* addon)
        {
            Visible = visible;
            Identity = identity;
            Text = text;
            Addon = addon;
        }

        public bool Visible { get; }
        public string Identity { get; }
        public string Text { get; }
        public AtkUnitBase* Addon { get; }
    }
}
