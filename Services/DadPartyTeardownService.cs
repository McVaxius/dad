using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using dad.Models;

namespace dad.Services;

internal sealed unsafe class DadPartyTeardownService
{
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private DadPartyTeardownController? controller;
    private string fallbackInviterName = string.Empty;
    private string lastDecisionDiagnostic = string.Empty;
    private DadPartyTeardownMutationMode mutationMode;

    public DadPartyTeardownService(
        IPartyList partyList,
        IPlayerState playerState,
        ICondition condition,
        IPluginLog log)
    {
        this.partyList = partyList;
        this.playerState = playerState;
        this.condition = condition;
        this.log = log;
    }

    public void Begin(IReadOnlyCollection<ulong> expectedMembers, ulong expectedLeaderContentId, string expectedLeaderName)
        => Begin(
            expectedMembers,
            expectedLeaderContentId,
            expectedLeaderContentId,
            expectedLeaderName,
            DadPartyTeardownMutationMode.DisbandAsLeader);

    public void Begin(
        IReadOnlyCollection<ulong> expectedMembers,
        ulong expectedLeaderContentId,
        ulong expectedLocalContentId,
        string expectedLeaderName,
        DadPartyTeardownMutationMode mode)
    {
        var prompt = ReadPrompt();
        fallbackInviterName = expectedLeaderName?.Trim() ?? string.Empty;
        lastDecisionDiagnostic = string.Empty;
        mutationMode = mode;
        controller = new DadPartyTeardownController(
            expectedMembers,
            expectedLeaderContentId,
            expectedLocalContentId,
            mode,
            DateTime.UtcNow,
            prompt.Visible,
            prompt.Identity);
    }

    public DadPartyDisbandPreflight GetCurrentPartyDisbandPreflight()
    {
        var isCrossRealmParty = InfoProxyCrossRealm.IsCrossRealmParty();
        var memberIds = isCrossRealmParty
            ? ReadCrossRealmMemberIds()
            : partyList.Select(static member => member.ContentId).Where(static id => id != 0).ToList();
        if (memberIds.Count == 0 && playerState.ContentId != 0)
            memberIds.Add(playerState.ContentId);

        var leaderContentId = 0UL;
        if (isCrossRealmParty)
        {
            if (InfoProxyCrossRealm.IsLocalPlayerPartyLeader())
                leaderContentId = playerState.ContentId;
        }
        else
        {
            var leaderIndex = partyList.PartyLeaderIndex;
            if (leaderIndex < partyList.Length)
                leaderContentId = partyList[(int)leaderIndex]?.ContentId ?? 0;
        }

        return DadCrewToolsRules.EvaluateDisband(
            playerState.ContentId,
            leaderContentId,
            memberIds,
            isCrossRealmParty,
            condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56],
            condition[ConditionFlag.InDutyQueue] ||
            condition[ConditionFlag.WaitingForDuty] ||
            condition[ConditionFlag.WaitingForDutyFinder],
            IsWorldStable());
    }

    public bool TryBeginCurrentParty(out DadPartyDisbandPreflight preflight)
    {
        preflight = GetCurrentPartyDisbandPreflight();
        if (!preflight.CanDisband)
            return false;

        Begin(
            preflight.MemberContentIds,
            preflight.LeaderContentId,
            preflight.LeaderName);
        return true;
    }

    public DadPartyTeardownDecision Update()
    {
        if (controller == null)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, "Party teardown controller was not initialized.");

        var prompt = ReadPrompt();
        var partyMenuAddon = RaptureAtkUnitManager.Instance()->GetAddonByName("PartyMemberList");
        var partyMenuVisible = partyMenuAddon != null && partyMenuAddon->IsVisible;
        var isCrossRealmParty = InfoProxyCrossRealm.IsCrossRealmParty();
        var memberIds = isCrossRealmParty
            ? ReadCrossRealmMemberIds()
            : partyList.Select(static member => member.ContentId).Where(static id => id != 0).ToList();
        if (memberIds.Count == 0 && playerState.ContentId != 0)
            memberIds.Add(playerState.ContentId);

        var leaderContentId = 0UL;
        if (isCrossRealmParty)
        {
            if (InfoProxyCrossRealm.IsLocalPlayerPartyLeader())
                leaderContentId = playerState.ContentId;
        }
        else
        {
            var leaderIndex = partyList.PartyLeaderIndex;
            if (leaderIndex < partyList.Length)
                leaderContentId = partyList[(int)leaderIndex]?.ContentId ?? 0;
        }

        var proxy = InfoProxyPartyInvite.Instance();
        var inviterName = proxy == null ? string.Empty : proxy->InviterName.ToString();
        if (string.IsNullOrWhiteSpace(inviterName))
            inviterName = fallbackInviterName;

        var isInDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56];
        var isQueued = condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDuty] || condition[ConditionFlag.WaitingForDutyFinder];
        var isWorldStable = IsWorldStable();
        var decision = controller.Pulse(new DadPartyTeardownObservation(
            DateTime.UtcNow,
            playerState.ContentId,
            leaderContentId,
            memberIds,
            isCrossRealmParty,
            isInDuty,
            isQueued,
            isWorldStable,
            partyMenuVisible,
            prompt.Visible,
            prompt.Identity,
            prompt.Text,
            inviterName));

        var diagnostic = string.Join(
            "|",
            decision.Action,
            decision.Summary,
            controller.CommandAttempts,
            playerState.ContentId,
            leaderContentId,
            string.Join(",", memberIds),
            isCrossRealmParty,
            isInDuty,
            isQueued,
            isWorldStable,
            partyMenuVisible,
            prompt.Visible,
            prompt.Identity);
        if (!string.Equals(lastDecisionDiagnostic, diagnostic, StringComparison.Ordinal))
        {
            lastDecisionDiagnostic = diagnostic;
            log.Information(
                "[dad] Party teardown decision action={Action} attempts={Attempts}/{MaximumAttempts} source={PartySource} crossRealm={CrossRealm} local={LocalContentId} leader={LeaderContentId} members={Members} inDuty={InDuty} queued={Queued} worldStable={WorldStable} partyMenuVisible={PartyMenuVisible} promptVisible={PromptVisible} prompt={PromptIdentity} summary={Summary}",
                decision.Action,
                controller.CommandAttempts,
                DadPartyTeardownController.MaximumAttempts,
                isCrossRealmParty ? "InfoProxyCrossRealm" : "PartyList",
                isCrossRealmParty,
                playerState.ContentId,
                leaderContentId,
                memberIds.Count == 0 ? "(none)" : string.Join(",", memberIds),
                isInDuty,
                isQueued,
                isWorldStable,
                partyMenuVisible,
                prompt.Visible,
                string.IsNullOrWhiteSpace(prompt.Identity) ? "(none)" : prompt.Identity,
                decision.Summary);
        }

        try
        {
            if (decision.Action == DadPartyTeardownAction.SendBreakup)
            {
                var command = mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader
                    ? DadPartyTeardownController.BreakupCommand
                    : DadPartyTeardownController.LeaveCommand;
                SubmitChatCommand(command);
                log.Information(
                    "[dad] Submitted exact chat command command={Command} characters={CharacterCount}. {Summary}",
                    command,
                    command.Length,
                    decision.Summary);
            }
            else if (decision.Action == DadPartyTeardownAction.InvokePartyMenuLeave)
            {
                if (!FirePartyMenuLeave(partyMenuAddon))
                    throw new InvalidOperationException("PartyMemberList disappeared before the cross-world leave callback could be fired.");

                log.Information(
                    "[dad] Fired PartyMemberList callback updateState=true values={Operation},{Argument}. {Summary}",
                    DadPartyTeardownController.PartyMenuLeaveCallbackOperation,
                    DadPartyTeardownController.PartyMenuLeaveCallbackArgument,
                    decision.Summary);
            }
            else if (decision.Action == DadPartyTeardownAction.ApprovePrompt)
            {
                if (!FireYes(prompt.Addon))
                    throw new InvalidOperationException("The newly observed SelectYesno prompt disappeared before Yes could be fired.");

                log.Information("[dad] Approved newly appeared SelectYesno breakup prompt. {Summary}", decision.Summary);
            }
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

    private static List<ulong> ReadCrossRealmMemberIds()
    {
        var memberIds = new List<ulong>();
        var memberCount = InfoProxyCrossRealm.GetPartyMemberCount();
        for (uint memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            var member = InfoProxyCrossRealm.GetGroupMember(memberIndex);
            if (member != null && member->ContentId != 0)
                memberIds.Add(member->ContentId);
        }

        return memberIds;
    }

    private static void SubmitChatCommand(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new InvalidOperationException("The native game UI module is unavailable for chat input.");

        var bytes = Encoding.UTF8.GetBytes(command);
        var utf8String = Utf8String.FromSequence(bytes);
        uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
    }

    public void Reset()
    {
        controller = null;
        fallbackInviterName = string.Empty;
        lastDecisionDiagnostic = string.Empty;
        mutationMode = DadPartyTeardownMutationMode.DisbandAsLeader;
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

    private static bool FireYes(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible)
            return false;

        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        addon->FireCallback(1, values, true);
        return true;
    }

    private static bool FirePartyMenuLeave(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible)
            return false;

        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = DadPartyTeardownController.PartyMenuLeaveCallbackOperation;
        values[1].Type = AtkValueType.Int;
        values[1].Int = DadPartyTeardownController.PartyMenuLeaveCallbackArgument;
        addon->FireCallback(2, values, true);
        return true;
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
