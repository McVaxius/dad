using dad.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace dad.Services;

internal sealed record DadAuthenticatedIslandInviteResult(string Summary);

internal sealed unsafe class InfoProxyPartyInviteGateway : IDadNativePartyInviteDispatcher
{
    private readonly Configuration configuration;
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly IPartyList partyList;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly IDadPartyNativeAccess? native;
    private readonly DadNativePartyInviteAttemptTracker inviteAttempts = new();
    private readonly DadPartyInvitationAcceptanceTracker acceptance = new();
    internal DadInviteDiagnostics Diagnostics { get; } = new();
    private string activeParticipantRunId = string.Empty;
    private DadExpectedPartyInviter? pendingExpectedInviter;
    private DadParticipantPartyDepartureController? departureController;
    private DadSelectYesnoPromptSnapshot baselineSelectYesnoPrompt;
    private string lastDepartureDiagnostic = string.Empty;
    private string departureFailure = string.Empty;
    private int approvedInvitationPromptAttempt;
    private ulong acceptanceLocalContentId;
    private DateTime? reassemblySoloSinceUtc;
    private DateTime? lastReassemblyObservationUtc;
    private bool reassemblyAcceptancePending;
    private string lastReassemblyDiagnostic = string.Empty;
    private DateTime nextReassemblyDiagnosticUtc = DateTime.MinValue;
    private string reassemblySoloBlocker = string.Empty;

    public InfoProxyPartyInviteGateway(
        Configuration configuration,
        IFramework framework,
        IPlayerState playerState,
        IPartyList partyList,
        ICondition condition,
        IPluginLog log,
        IDadPartyNativeAccess? native = null)
    {
        this.configuration = configuration;
        this.framework = framework;
        this.playerState = playerState;
        this.partyList = partyList;
        this.condition = condition;
        this.log = log;
        this.native = native;
    }

    public void BeginParticipantRun(string runId)
    {
        RequireFrameworkThread();
        runId = runId?.Trim() ?? string.Empty;
        var newRun = !string.Equals(activeParticipantRunId, runId, StringComparison.OrdinalIgnoreCase);
        if (newRun)
        {
            activeParticipantRunId = runId;
            pendingExpectedInviter = null;
            departureController = null;
            baselineSelectYesnoPrompt = ReadSelectYesnoPrompt().Snapshot;
            lastDepartureDiagnostic = string.Empty;
            departureFailure = string.Empty;
            approvedInvitationPromptAttempt = 0;
            acceptanceLocalContentId = 0;
            reassemblyAcceptancePending = false;
            lastReassemblyDiagnostic = string.Empty;
            nextReassemblyDiagnosticUtc = DateTime.MinValue;
            reassemblySoloBlocker = string.Empty;
            ResetReassemblyObservation();
        }

        var baseline = ReadPendingInvitation();
        if (configuration.DebugUiEnabled && newRun)
            Diagnostics.Observe(DadClock.UtcNow, $"receiver-run request={runId} baselinePresent={baseline.IsPresent} baselineInviteTime={baseline.InviteTime}", "run");
        acceptance.BeginRun(runId, baseline);
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
            acceptanceLocalContentId = playerState.ContentId;
            var prompt = ReadSelectYesnoPrompt();
            departureController = new DadParticipantPartyDepartureController(
                inviter.ContentId,
                DadClock.UtcNow,
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
            reassemblyAcceptancePending = false;
            ResetReassemblyObservation();
            acceptance.ShouldAccept(ReadPendingInvitation(), partyListContainsExpectedContentId: true, DadClock.UtcNow);
            blocker = string.Empty;
            return true;
        }

        if (acceptance.RequiresReassembly)
        {
            if (!acceptance.TryRearmForAssembly(inviter, ReadPendingInvitation(),
                    ObserveStableSoloForReassembly(DadClock.UtcNow), out blocker))
            {
                if (!string.IsNullOrEmpty(reassemblySoloBlocker))
                    blocker += " " + reassemblySoloBlocker;
                LogReassemblyWait(inviter, blocker, DadClock.UtcNow);
                return false;
            }
            reassemblyAcceptancePending = true;
            log.Information("[dad] Party invitation acceptance rearmed for same-run assembly request={RequestId} inviter={Inviter} worker={WorkerSessionId}.",
                inviter.RunId, inviter.CharacterKey, inviter.WorkerSessionId);
        }

        blocker = acceptance.BuildRetryStatus(
            DadClock.UtcNow,
            TimeSpan.FromSeconds(Math.Max(10, configuration.AssemblyTimeoutSeconds)));
        return false;
    }

    internal IReadOnlyList<DadNativePartyInviteTarget> SelectAssemblyInviteTargets(
        string runId,
        IReadOnlyList<DadNativePartyInviteTarget> targets,
        IReadOnlyList<DadPartyMemberSnapshot> members)
        // Exact diagnostic requests (including revoked ones) keep their scoped policy.
        => string.Equals(Diagnostics.RunId, runId, StringComparison.Ordinal)
            ? targets
            : DadNativePartyInviteRules.SelectSerialTargets(targets, members);

    public DadNativePartyInviteAttempt? TryInvite(
        DadNativePartyInviteTarget target,
        bool partyListContainsContentId,
        out string blocker)
    {
        RequireFrameworkThread();
        var comparisonRequest = string.Equals(Diagnostics.RunId, target.RunId, StringComparison.Ordinal);
        DadNativePartyInviteType? forcedType = null;
        if (comparisonRequest &&
            !Diagnostics.Allows(target.RunId, target.ContentId,
                ReadAuthoritativePartyMembers().Select(member => member.ContentId).ToArray(),
                DadClock.UtcNow, configuration.DebugUiEnabled && configuration.PluginEnabled, out forcedType))
        {
            blocker = string.Empty; // A sequential recipient is pending, not a failed participant.
            return null;
        }
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
            LocalCurrentWorldId = native?.Observe().CurrentWorldId ?? (uint)playerState.CurrentWorld.RowId,
            // Home World does not prove visited-current-world truth. Keep the original native
            // branch/fallback selection; serial assembly changes recipient order only.
            WorldRelationExact = false,
            // Territory equality cannot prove the same duty instance. This becomes true only
            // when a future runtime source can establish exact same-applicable-instance truth.
            SameApplicableInstanceExact = target.SameApplicableInstanceExact,
        };
        var attempt = inviteAttempts.TryDispatch(
            runtimeTarget,
            partyListContainsContentId,
            DadClock.UtcNow,
            this,
            out blocker,
            forcedType);
        if (configuration.DebugUiEnabled && attempt != null)
            Diagnostics.Record(DadClock.UtcNow, $"send request={target.RunId} slot={target.SlotId} type={attempt.InviteType} attempt={attempt.AttemptNumber} nativeReturn={attempt.DispatchResult} delivery=unproven");
        if (configuration.DebugUiEnabled && partyListContainsContentId)
            Diagnostics.Observe(DadClock.UtcNow, $"sender-member request={target.RunId} slot={target.SlotId} joined=true", "member-" + target.SlotId);
        return attempt;
    }

    internal DadAuthenticatedIslandInviteResult? TryInviteAuthenticatedIsland(
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
            LocalCurrentWorldId = native?.Observe().CurrentWorldId ?? (uint)playerState.CurrentWorld.RowId,
            WorldRelationExact = false,
            SameApplicableInstanceExact = target.SameApplicableInstanceExact,
        };
        var attempts = inviteAttempts.TryDispatchAuthenticatedIsland(
            runtimeTarget,
            partyListContainsContentId,
            DadClock.UtcNow,
            this,
            out blocker);
        if (attempts.Count == 0)
            return null;
        var pass = attempts[0].AttemptNumber;
        var sameWorld = attempts.Single(static attempt =>
            attempt.InviteType == DadNativePartyInviteType.SameWorld).DispatchResult;
        var crossWorld = attempts.Single(static attempt =>
            attempt.InviteType == DadNativePartyInviteType.CrossWorldContentId).DispatchResult;
        return new DadAuthenticatedIslandInviteResult(
            $"Dad-island invite pass {pass}: same-world={sameWorld}, cross-world={crossWorld} for {target.CharacterKey}.");
    }

    public bool ConfirmRunPartyMembership(string runId)
    {
        RequireFrameworkThread();
        acceptance.ConfirmPartyMembership();
        return inviteAttempts.ConfirmRun(runId);
    }

    public IReadOnlyList<DadPartyMemberSnapshot> ReadAuthoritativePartyMembers()
        => ReadAuthoritativePartyMembersCore()
            .DistinctBy(static member => member.ContentId)
            .Select(static member => member.Clone())
            .ToList();

    internal IReadOnlyList<DadPartyMemberSnapshot> ReadAuthoritativePartyMembersIncludingDuplicates()
        => ReadAuthoritativePartyMembersCore()
            .Select(static member => member.Clone())
            .ToList();

    private List<DadPartyMemberSnapshot> ReadAuthoritativePartyMembersCore()
    {
        RequireFrameworkThread();
        if (native != null) return native.Observe().Members.Select(member => member.Clone()).ToList();
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

        return members;
    }

    public void UpdateAcceptance()
    {
        RequireFrameworkThread();
        if (configuration.DebugUiEnabled)
        {
            var pending = ReadPendingInvitation();
            var inviter = pendingExpectedInviter ?? acceptance.ExpectedInviter;
            Diagnostics.Observe(DadClock.UtcNow,
                $"receive request={activeParticipantRunId} present={pending.IsPresent} inviteTime={pending.InviteTime} inviterWorld={pending.InviterWorldId} expectedArmed={acceptance.ExpectedInviter != null} expectedNameMatch={inviter != null && string.Equals(pending.InviterName, inviter.CharacterName, StringComparison.Ordinal)} expectedWorldMatch={inviter != null && pending.InviterWorldId == inviter.WorldId} departurePending={departureController != null}", "pending");
        }
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
        var nowUtc = DadClock.UtcNow;
        if (partyContainsInviter)
        {
            reassemblyAcceptancePending = false;
            ResetReassemblyObservation();
        }
        else if (acceptance.RequiresReassembly || reassemblyAcceptancePending)
        {
            var safeSolo = ObserveStableSoloForReassembly(nowUtc); // Observation alone cannot rearm acceptance.
            if (acceptance.RequiresReassembly)
                LogReassemblyWait(expected, "Confirmed membership was lost; awaiting a validated JoinParty instruction to rearm acceptance. " + reassemblySoloBlocker, nowUtc);
            if (reassemblyAcceptancePending && !safeSolo)
                return;
        }
        var eligibility = acceptance.EvaluateAcceptance(invitation, partyContainsInviter, nowUtc);
        if (configuration.DebugUiEnabled)
            Diagnostics.Observe(nowUtc, $"acceptance request={activeParticipantRunId} decision={eligibility} partyContainsInviter={partyContainsInviter}", "eligibility");
        if (eligibility != "eligible")
            return;

        var proxy = native == null ? InfoProxyPartyInvite.Instance() : null;
        if (proxy == null && native == null)
            return;

        var promptBefore = ReadSelectYesnoPrompt();
        var restoreDispatched = false;
        if (DadPartyInvitePromptOwnershipRules.ShouldRestoreHiddenPrompt(invitation, expected, promptBefore.Snapshot))
            restoreDispatched = FireNotificationInviteRestore();

        var revalidated = native?.Observe().Invitation ?? ReadPendingInvitation(proxy);
        if (revalidated != invitation ||
            !DadPartyInvitePromptOwnershipRules.IsExactPendingInvitation(revalidated, expected))
        {
            return;
        }

        var promptAfter = ReadSelectYesnoPrompt();
        bool nativeResponded;
        try
        {
            nativeResponded = native != null ? native.RespondToInvitation(revalidated) : proxy->RespondToInvitation(proxy->InviterName.StringPtr, true);
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
        if (configuration.DebugUiEnabled)
            Diagnostics.Record(nowUtc, $"respond request={expected.RunId} inviteTime={invitation.InviteTime} restored={restoreDispatched} nativeReturn={nativeResponded} directYes={directYes} membership=unproven");
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
        => ResetCore(preserveComparison: false);

    // Admission resets the normal invite state before the first assembly tick. Preserve only
    // an explicitly armed comparison for that exact newly admitted request, never on cleanup.
    internal void ResetForRun(string runId)
        => ResetCore(preserveComparison: Diagnostics.Active &&
            string.Equals(Diagnostics.RunId, runId, StringComparison.Ordinal));

    private void ResetCore(bool preserveComparison)
    {
        if (!preserveComparison)
            Diagnostics.Finish(DadClock.UtcNow, "Gateway reset; comparison policy revoked.");
        inviteAttempts.Clear();
        acceptance.Clear();
        activeParticipantRunId = string.Empty;
        pendingExpectedInviter = null;
        departureController = null;
        baselineSelectYesnoPrompt = default;
        lastDepartureDiagnostic = string.Empty;
        departureFailure = string.Empty;
        approvedInvitationPromptAttempt = 0;
        acceptanceLocalContentId = 0;
        reassemblyAcceptancePending = false;
        lastReassemblyDiagnostic = string.Empty;
        nextReassemblyDiagnosticUtc = DateTime.MinValue;
        reassemblySoloBlocker = string.Empty;
        ResetReassemblyObservation();
    }

    private bool ObserveStableSoloForReassembly(DateTime nowUtc)
    {
        var members = ReadAuthoritativePartyMembers();
        var crossRealm = native?.Observe().CrossRealm ?? InfoProxyCrossRealm.IsCrossRealmParty();
        var localContentId = playerState.ContentId;
        if (acceptanceLocalContentId == 0 || localContentId != acceptanceLocalContentId || crossRealm ||
            members.Count != 1 || members[0].ContentId != localContentId || !IsWorldStable() ||
            condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] ||
            condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDuty] ||
            condition[ConditionFlag.WaitingForDutyFinder])
        {
            ResetReassemblyObservation();
            reassemblySoloBlocker = $"Solo proof unavailable: local={localContentId}, originalLocal={acceptanceLocalContentId}, crossRealm={crossRealm}, members={string.Join(',', members.Select(member => member.ContentId))}, worldStable={IsWorldStable()}, inDuty={condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]}, queued={condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDuty] || condition[ConditionFlag.WaitingForDutyFinder]}.";
            return false;
        }

        if (lastReassemblyObservationUtc is { } previous &&
            (nowUtc < previous || nowUtc - previous > DadParticipantPartyDepartureController.SoloConfirmationInterval))
            reassemblySoloSinceUtc = null;
        lastReassemblyObservationUtc = nowUtc;
        reassemblySoloSinceUtc ??= nowUtc;
        var stable = nowUtc - reassemblySoloSinceUtc.Value >= DadParticipantPartyDepartureController.SoloConfirmationInterval;
        reassemblySoloBlocker = stable ? string.Empty : "Solo proof is waiting for the continuous confirmation interval.";
        return stable;
    }

    private void LogReassemblyWait(DadExpectedPartyInviter expected, string blocker, DateTime nowUtc)
    {
        // Bound diagnostics even when native observations or JoinParty retries oscillate.
        if (nowUtc < nextReassemblyDiagnosticUtc)
            return;
        if (string.Equals(lastReassemblyDiagnostic, blocker, StringComparison.Ordinal))
            nextReassemblyDiagnosticUtc = nowUtc.AddSeconds(30);
        else
            nextReassemblyDiagnosticUtc = nowUtc.AddSeconds(5);
        lastReassemblyDiagnostic = blocker;
        var invitation = ReadPendingInvitation();
        log.Information("[dad] Party reassembly wait request={RequestId} inviter={Inviter} inviteTime={InviteTime} pendingName={PendingName} pendingWorld={PendingWorld} reason={Reason}",
            expected.RunId, expected.CharacterKey, invitation.InviteTime, invitation.InviterName, invitation.InviterWorldId, blocker);
    }

    private void ResetReassemblyObservation()
    {
        reassemblySoloSinceUtc = null;
        lastReassemblyObservationUtc = null;
    }

    public bool InviteSameWorld(ulong contentId, string exactCharacterName, ushort worldId)
    {
        RequireFrameworkThread();
        if (native != null) return native.InviteSameWorld(contentId, exactCharacterName, worldId);
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
        if (native != null) return native.InviteCrossWorld(contentId, worldId);
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
        if (native != null) return native.InviteInInstance(contentId);
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
        var partyMenu = native == null ? GetAddon("PartyMemberList") : null;
        var crossRealm = native?.Observe().CrossRealm ?? InfoProxyCrossRealm.IsCrossRealmParty();
        var members = ReadAuthoritativePartyMemberIds(crossRealm);
        var decision = departureController.Pulse(new DadParticipantPartyDepartureObservation(
            DadClock.UtcNow,
            playerState.ContentId,
            pendingExpectedInviter.ContentId,
            members,
            crossRealm,
            condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56],
            condition[ConditionFlag.InDutyQueue] || condition[ConditionFlag.WaitingForDuty] || condition[ConditionFlag.WaitingForDutyFinder],
            IsWorldStable(),
            native?.Observe().PartyMenuVisible ?? (partyMenu != null && partyMenu->IsVisible),
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
        => contentId != 0 && ReadAuthoritativePartyMembers().Any(member => member.ContentId == contentId);

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

    internal bool IsInviteTestSafe()
        => IsWorldStable() &&
           !condition[ConditionFlag.BoundByDuty] && !condition[ConditionFlag.BoundByDuty56] &&
           !condition[ConditionFlag.InDutyQueue] && !condition[ConditionFlag.WaitingForDuty] &&
           !condition[ConditionFlag.WaitingForDutyFinder];

    private static DadPendingPartyInvitation ReadPendingInvitation(InfoProxyPartyInvite* proxy)
        => proxy == null
            ? default
            : new DadPendingPartyInvitation(
                proxy->InviteTime,
                proxy->InviterName.ToString(),
                proxy->InviterWorldId);

    private DadPendingPartyInvitation ReadPendingInvitation()
        => native?.Observe().Invitation ?? ReadPendingInvitation(InfoProxyPartyInvite.Instance());

    private PromptRuntimeSnapshot ReadSelectYesnoPrompt()
    {
        if (native != null) return new(native.Observe().Prompt, null);
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

    private bool IsOtherReadyPromptVisible()
    {
        if (native != null) return native.Observe().OtherReadyPromptVisible;
        var privatePrompt = GetAddon("LookingForGroupPrivate");
        return privatePrompt != null && privatePrompt->IsVisible && privatePrompt->IsReady;
    }

    private static AtkUnitBase* GetAddon(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        return manager == null ? null : manager->GetAddonByName(name);
    }

    private bool FireNotificationInviteRestore()
    {
        if (native != null) return native.RestoreInvitationPrompt();
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

    private bool FireYes(AtkUnitBase* addon)
    {
        if (native != null) return native.ApprovePrompt();
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        addon->FireCallback(1, values, true);
        return true;
    }

    private bool FirePartyMenuLeave(AtkUnitBase* addon)
    {
        if (native != null) return native.LeaveThroughPartyMenu();
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

    private void SubmitLeaveChatCommand()
    {
        if (native != null) { native.SendChat(DadParticipantPartyDepartureController.LeaveCommand); return; }
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
