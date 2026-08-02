using dad.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

internal sealed unsafe class InfoProxyPartyInviteGateway : IDadNativePartyInviteDispatcher
{
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly IPartyList partyList;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly DadNativePartyInviteAttemptTracker inviteAttempts = new();
    private readonly DadPartyInvitationAcceptanceTracker acceptance = new();
    private string activeParticipantRunId = string.Empty;
    private DadExpectedPartyInviter? pendingExpectedInviter;
    private DadParticipantPartyDepartureController? departureController;
    private DadSelectYesnoPromptSnapshot baselineSelectYesnoPrompt;
    private string lastDepartureDiagnostic = string.Empty;
    private string departureFailure = string.Empty;
    private int approvedInvitationPromptAttempt;

    public InfoProxyPartyInviteGateway(
        Configuration configuration,
        IFramework framework,
        IPlayerState playerState,
        IPartyList partyList,
        ICondition condition,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.framework = framework;
        this.playerState = playerState;
        this.partyList = partyList;
        this.condition = condition;
        this.log = log;
    }

    public void BeginParticipantRun(string runId)
    {
        RequireFrameworkThread();
        runId = runId?.Trim() ?? string.Empty;
        if (!string.Equals(activeParticipantRunId, runId, StringComparison.OrdinalIgnoreCase))
        {
            activeParticipantRunId = runId;
            pendingExpectedInviter = null;
            departureController = null;
            baselineSelectYesnoPrompt = ReadSelectYesnoPrompt().Snapshot;
            lastDepartureDiagnostic = string.Empty;
            departureFailure = string.Empty;
            approvedInvitationPromptAttempt = 0;
        }

        acceptance.BeginRun(runId, ReadPendingInvitation());
    }

    public bool TryArmAcceptance(DadExpectedPartyInviter inviter, out string blocker)
    {
        RequireFrameworkThread();
        blocker = DadPartyInvitationAcceptanceTracker.Validate(inviter);
        if (!string.IsNullOrWhiteSpace(blocker))
            return false;
        if (!string.Equals(activeParticipantRunId, inviter.RunId, StringComparison.OrdinalIgnoreCase))
        {
            blocker = $"Party invitation acceptance is prepared for run '{activeParticipantRunId}', not '{inviter.RunId}'.";
            return false;
        }

        if (pendingExpectedInviter == null)
        {
            pendingExpectedInviter = inviter;
            var prompt = ReadSelectYesnoPrompt();
            departureController = new DadParticipantPartyDepartureController(
                inviter.ContentId,
                DateTime.UtcNow,
                prompt.Snapshot.Visible,
                prompt.Snapshot.Identity,
                prompt.Snapshot.Ready,
                prompt.Snapshot.Text,
                configuration.AllowFreshUnprovenPromptApproval);
        }
        else if (!DadPartyInvitationAcceptanceTracker.SameExpectedInviter(pendingExpectedInviter, inviter))
        {
            blocker = "Party invitation inviter identity changed after it was frozen for this run.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(departureFailure))
        {
            blocker = departureFailure;
            return false;
        }

        if (departureController != null)
        {
            var departure = AdvanceParticipantDeparture();
            if (departure.Action == DadParticipantPartyDepartureAction.Fail)
            {
                departureFailure = departure.Summary;
                blocker = departureFailure;
                return false;
            }
            if (departure.Action != DadParticipantPartyDepartureAction.Complete)
            {
                blocker = departure.Summary;
                return false;
            }

            departureController = null;
        }

        if (!acceptance.TryArm(inviter, out blocker))
            return false;

        if (PartyListContains(inviter.ContentId))
        {
            acceptance.ShouldAccept(default, partyListContainsExpectedContentId: true, DateTime.UtcNow);
            blocker = string.Empty;
            return true;
        }

        blocker = acceptance.BuildRetryStatus(
            DateTime.UtcNow,
            TimeSpan.FromSeconds(Math.Max(10, configuration.AssemblyTimeoutSeconds)));
        return false;
    }

    public DadNativePartyInviteAttempt? TryInvite(
        DadNativePartyInviteTarget target,
        bool partyListContainsContentId,
        out string blocker)
    {
        RequireFrameworkThread();
        var runtimeTarget = new DadNativePartyInviteTarget
        {
            RunId = target.RunId,
            ModuleId = target.ModuleId,
            SlotId = target.SlotId,
            AccountKey = target.AccountKey,
            CharacterKey = target.CharacterKey,
            ContentId = target.ContentId,
            CharacterName = target.CharacterName,
            WorldId = target.WorldId,
            WorkerSessionId = target.WorkerSessionId,
            LocalCurrentWorldId = (uint)playerState.CurrentWorld.RowId,
            // DAD has X's frozen/home World ID, not X's visited-current-world truth. Keep the
            // relation ambiguous so attempt two uses the alternate native branch.
            WorldRelationExact = false,
            // Territory equality cannot prove the same duty instance. This becomes true only
            // when a future runtime source can establish exact same-applicable-instance truth.
            SameApplicableInstanceExact = target.SameApplicableInstanceExact,
        };
        return inviteAttempts.TryDispatch(
            runtimeTarget,
            partyListContainsContentId,
            DateTime.UtcNow,
            this,
            out blocker);
    }

    public bool ConfirmRunPartyMembership(string runId)
    {
        RequireFrameworkThread();
        acceptance.ConfirmPartyMembership();
        return inviteAttempts.ConfirmRun(runId);
    }

    public IReadOnlyList<DadPartyMemberSnapshot> ReadAuthoritativePartyMembers()
    {
        RequireFrameworkThread();
        var members = new List<DadPartyMemberSnapshot>();
        if (InfoProxyCrossRealm.IsCrossRealmParty())
        {
            var count = InfoProxyCrossRealm.GetPartyMemberCount();
            for (uint index = 0; index < count; index++)
            {
                var member = InfoProxyCrossRealm.GetGroupMember(index);
                if (member == null || member->ContentId == 0)
                    continue;
                members.Add(new DadPartyMemberSnapshot
                {
                    ContentId = member->ContentId,
                    CharacterName = member->NameString,
                    IsLocalPlayer = member->ContentId == playerState.ContentId,
                });
            }
        }
        else
        {
            members.AddRange(partyList
                .Where(static member => member.ContentId != 0)
                .Select(member => new DadPartyMemberSnapshot
                {
                    ContentId = member.ContentId,
                    CharacterName = member.Name.ToString(),
                    IsLocalPlayer = member.ContentId == playerState.ContentId,
                }));
        }

        if (playerState.ContentId != 0 && members.All(member => member.ContentId != playerState.ContentId))
        {
            members.Add(new DadPartyMemberSnapshot
            {
                ContentId = playerState.ContentId,
                IsLocalPlayer = true,
            });
        }

        return members
            .DistinctBy(static member => member.ContentId)
            .Select(static member => member.Clone())
            .ToList();
    }

    public void UpdateAcceptance()
    {
        RequireFrameworkThread();
        if (departureController != null)
        {
            var departure = AdvanceParticipantDeparture();
            if (departure.Action == DadParticipantPartyDepartureAction.Fail)
            {
                departureFailure = departure.Summary;
                return;
            }
            if (departure.Action != DadParticipantPartyDepartureAction.Complete)
                return;

            departureController = null;
            if (pendingExpectedInviter == null || !acceptance.TryArm(pendingExpectedInviter, out _))
                return;
        }

        var expected = acceptance.ExpectedInviter;
        if (expected == null)
            return;

        var partyContainsInviter = PartyListContains(expected.ContentId);
        var invitation = ReadPendingInvitation();
        var nowUtc = DateTime.UtcNow;
        if (!acceptance.ShouldAccept(invitation, partyContainsInviter, nowUtc))
            return;

        var proxy = InfoProxyPartyInvite.Instance();
        if (proxy == null)
            return;

        var promptBefore = ReadSelectYesnoPrompt();
        var restoreDispatched = false;
        if (DadPartyInvitePromptOwnershipRules.ShouldRestoreHiddenPrompt(invitation, expected, promptBefore.Snapshot))
            restoreDispatched = FireNotificationInviteRestore();

        var revalidated = ReadPendingInvitation(proxy);
        if (revalidated != invitation ||
            !DadPartyInvitePromptOwnershipRules.IsExactPendingInvitation(revalidated, expected))
        {
            return;
        }

        var promptAfter = ReadSelectYesnoPrompt();
        bool nativeResponded;
        try
        {
            nativeResponded = proxy->RespondToInvitation(proxy->InviterName.StringPtr, true);
        }
        catch (Exception ex)
        {
            nativeResponded = false;
            log.Warning(ex,
                "[dad] Native party acceptance threw request={RequestId} account={AccountKey} character={CharacterKey} contentId={ContentId} world={WorldId} worker={WorkerSessionId} inviteTime={InviteTime}.",
                expected.RunId,
                expected.AccountKey,
                expected.CharacterKey,
                expected.ContentId,
                expected.WorldId,
                expected.WorkerSessionId,
                invitation.InviteTime);
        }

        var directYes = false;
        var promptDecision = default(DadPromptApprovalDecision);
        var currentAttempt = acceptance.AttemptCount + 1;
        if (!nativeResponded &&
            DadPartyInvitePromptOwnershipRules.CanUseDirectYes(
                invitation,
                revalidated,
                expected,
                baselineSelectYesnoPrompt,
                promptBefore.Snapshot,
                promptAfter.Snapshot,
                restoreDispatched,
                currentAttempt,
                approvedInvitationPromptAttempt,
                soleReadyPrompt: !IsOtherReadyPromptVisible(),
                configuration.AllowFreshUnprovenPromptApproval,
                out promptDecision))
        {
            directYes = FireYes(promptAfter.Addon);
            if (directYes)
            {
                approvedInvitationPromptAttempt = currentAttempt;
                if (promptDecision.UsedOverride)
                {
                    log.Warning(
                        "[dad] Prompt ownership override used operation=party-invitation request={RequestId} attempt={Attempt} prompt={PromptIdentity} warning={Warning}",
                        expected.RunId,
                        currentAttempt,
                        promptAfter.Snapshot.Identity,
                        promptDecision.Summary);
                }
            }
        }

        acceptance.RecordAttempt(invitation, nowUtc);
        log.Information(
            "[dad] Native party acceptance request={RequestId} account={AccountKey} character={CharacterKey} contentId={ContentId} world={WorldId} worker={WorkerSessionId} inviteTime={InviteTime} restored={Restored} nativeResponded={NativeResponded} directYes={DirectYes} partyList={PartyListResult}.",
            expected.RunId,
            expected.AccountKey,
            expected.CharacterKey,
            expected.ContentId,
            expected.WorldId,
            expected.WorkerSessionId,
            invitation.InviteTime,
            restoreDispatched,
            nativeResponded,
            directYes,
            partyContainsInviter);
    }

    public void Reset()
    {
        inviteAttempts.Clear();
        acceptance.Clear();
        activeParticipantRunId = string.Empty;
        pendingExpectedInviter = null;
        departureController = null;
        baselineSelectYesnoPrompt = default;
        lastDepartureDiagnostic = string.Empty;
        departureFailure = string.Empty;
        approvedInvitationPromptAttempt = 0;
    }

    public bool InviteSameWorld(ulong contentId, string exactCharacterName, ushort worldId)
    {
        RequireFrameworkThread();
        try
        {
            var proxy = InfoProxyPartyInvite.Instance();
            return proxy != null && proxy->InviteToParty(contentId, exactCharacterName, worldId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Native same-world party invitation threw for Content ID {ContentId} World {WorldId}.", contentId, worldId);
            return false;
        }
    }

    public bool InviteCrossWorld(ulong contentId, ushort worldId)
    {
        RequireFrameworkThread();
        try
        {
            var proxy = InfoProxyPartyInvite.Instance();
            return proxy != null && proxy->InviteToPartyContentId(contentId, worldId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Native cross-world party invitation threw for Content ID {ContentId} World {WorldId}.", contentId, worldId);
            return false;
        }
    }

    public bool InviteInInstance(ulong contentId)
    {
        RequireFrameworkThread();
        try
        {
            var proxy = InfoProxyPartyInvite.Instance();
            return proxy != null && proxy->InviteToPartyInInstanceByContentId(contentId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Native in-instance party invitation threw for Content ID {ContentId}.", contentId);
            return false;
        }
    }

    private DadParticipantPartyDepartureDecision AdvanceParticipantDeparture()
    {
        if (departureController == null || pendingExpectedInviter == null)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Fail,
                "Participant party departure has no frozen controller or expected inviter.");
        }

        var prompt = ReadSelectYesnoPrompt();
        var partyMenu = GetAddon("PartyMemberList");
        var crossRealm = InfoProxyCrossRealm.IsCrossRealmParty();
        var members = ReadAuthoritativePartyMemberIds(crossRealm);
        var decision = departureController.Pulse(new DadParticipantPartyDepartureObservation(
            DateTime.UtcNow,
            playerState.ContentId,
            pendingExpectedInviter.ContentId,
            members,
            crossRealm,
            condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56],
            condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDuty] || condition[ConditionFlag.WaitingForDutyFinder],
            IsWorldStable(),
            partyMenu != null && partyMenu->IsVisible,
            prompt.Snapshot.Visible,
            prompt.Snapshot.Identity,
            prompt.Snapshot.Text,
            prompt.Snapshot.Ready,
            IsOtherReadyPromptVisible()));

        var diagnostic = $"{decision.Action}|{decision.Summary}|{departureController.CommandAttempts}|{crossRealm}|{string.Join(',', members)}|{prompt.Snapshot.Identity}";
        if (!string.Equals(lastDepartureDiagnostic, diagnostic, StringComparison.Ordinal))
        {
            lastDepartureDiagnostic = diagnostic;
            log.Information(
                "[dad] Participant party departure action={Action} attempts={Attempts}/{MaximumAttempts} source={PartySource} crossRealm={CrossRealm} members={Members} prompt={PromptIdentity} summary={Summary}",
                decision.Action,
                departureController.CommandAttempts,
                DadParticipantPartyDepartureController.MaximumAttempts,
                crossRealm ? "InfoProxyCrossRealm" : "PartyList",
                crossRealm,
                members.Count == 0 ? "(none)" : string.Join(",", members),
                string.IsNullOrWhiteSpace(prompt.Snapshot.Identity) ? "(none)" : prompt.Snapshot.Identity,
                decision.Summary);
        }

        try
        {
            switch (decision.Action)
            {
                case DadParticipantPartyDepartureAction.SendLeave:
                    SubmitLeaveChatCommand();
                    break;
                case DadParticipantPartyDepartureAction.InvokePartyMenuLeave:
                    if (!FirePartyMenuLeave(partyMenu))
                        throw new InvalidOperationException("PartyMemberList disappeared before the participant leave callback could be fired.");
                    break;
                case DadParticipantPartyDepartureAction.ApprovePrompt:
                    if (!FireYes(prompt.Addon))
                        throw new InvalidOperationException("The fresh participant leave confirmation disappeared before Yes could be fired.");
                    if (decision.PromptOverrideUsed)
                    {
                        log.Warning(
                            "[dad] Prompt ownership override used operation=participant-party-departure request={RequestId} attempt={Attempt} prompt={PromptIdentity} warning={Warning}",
                            activeParticipantRunId,
                            departureController.CommandAttempts,
                            prompt.Snapshot.Identity,
                            decision.PromptAudit);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[dad] Participant party departure mutation failed.");
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Fail,
                $"Participant party departure mutation failed: {ex.Message}");
        }

        return decision;
    }

    private bool PartyListContains(ulong contentId)
        => contentId != 0 && ReadAuthoritativePartyMemberIds(InfoProxyCrossRealm.IsCrossRealmParty()).Contains(contentId);

    private IReadOnlyList<ulong> ReadAuthoritativePartyMemberIds(bool crossRealm)
        => ReadAuthoritativePartyMembers()
            .Select(static member => member.ContentId)
            .Where(static contentId => contentId != 0)
            .ToList();

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

    private static DadPendingPartyInvitation ReadPendingInvitation(InfoProxyPartyInvite* proxy)
        => proxy == null
            ? default
            : new DadPendingPartyInvitation(
                proxy->InviteTime,
                proxy->InviterName.ToString(),
                proxy->InviterWorldId);

    private static DadPendingPartyInvitation ReadPendingInvitation()
        => ReadPendingInvitation(InfoProxyPartyInvite.Instance());

    private static PromptRuntimeSnapshot ReadSelectYesnoPrompt()
    {
        var addon = GetAddon("SelectYesno");
        if (addon == null || !addon->IsVisible)
            return default;

        var selectYesno = (AddonSelectYesno*)addon;
        var text = selectYesno->PromptText == null
            ? string.Empty
            : selectYesno->PromptText->NodeText.ToString().Trim();
        return new PromptRuntimeSnapshot(
            new DadSelectYesnoPromptSnapshot(
                true,
                addon->IsReady,
                $"{(nint)addon:X}",
                text),
            addon);
    }

    private static bool IsOtherReadyPromptVisible()
    {
        var privatePrompt = GetAddon("LookingForGroupPrivate");
        return privatePrompt != null && privatePrompt->IsVisible && privatePrompt->IsReady;
    }

    private static AtkUnitBase* GetAddon(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : manager->GetAddonByName(name);
    }

    private static bool FireNotificationInviteRestore()
    {
        var addon = GetAddon("_Notification");
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        values[1].Type = AtkValueType.Int;
        values[1].Int = 16;
        addon->FireCallback(2, values, true);
        return true;
    }

    private static bool FireYes(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible || !addon->IsReady)
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
        values[0].Int = DadParticipantPartyDepartureController.PartyMenuLeaveCallbackOperation;
        values[1].Type = AtkValueType.Int;
        values[1].Int = DadParticipantPartyDepartureController.PartyMenuLeaveCallbackArgument;
        addon->FireCallback(2, values, true);
        return true;
    }

    private static void SubmitLeaveChatCommand()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new InvalidOperationException("The native game UI module is unavailable for chat input.");

        Utf8String* utf8String = null;
        try
        {
            utf8String = Utf8String.FromString(DadParticipantPartyDepartureController.LeaveCommand);
            if (utf8String == null)
                throw new InvalidOperationException("The native /leave chat entry could not be allocated.");
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
        }
        finally
        {
            if (utf8String != null)
                utf8String->Dtor(true);
        }
    }

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("InfoProxyPartyInvite may only be accessed on the framework thread.");
    }

    private readonly struct PromptRuntimeSnapshot
    {
        public PromptRuntimeSnapshot(DadSelectYesnoPromptSnapshot snapshot, AtkUnitBase* addon)
        {
            Snapshot = snapshot;
            Addon = addon;
        }

        public DadSelectYesnoPromptSnapshot Snapshot { get; }
        public AtkUnitBase* Addon { get; }
    }
}
