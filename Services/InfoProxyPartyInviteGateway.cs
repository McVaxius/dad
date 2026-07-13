using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using dad.Models;

namespace dad.Services;

internal sealed unsafe class InfoProxyPartyInviteGateway : IDadNativePartyInviteDispatcher
{
    private readonly IFramework framework;
    private readonly IPlayerState playerState;
    private readonly IPartyList partyList;
    private readonly IPluginLog log;
    private readonly DadNativePartyInviteAttemptTracker inviteAttempts = new();
    private readonly DadPartyInvitationAcceptanceTracker acceptance = new();

    public InfoProxyPartyInviteGateway(
        IFramework framework,
        IPlayerState playerState,
        IPartyList partyList,
        IPluginLog log)
    {
        this.framework = framework;
        this.playerState = playerState;
        this.partyList = partyList;
        this.log = log;
    }

    public void BeginParticipantRun(string runId)
    {
        RequireFrameworkThread();
        acceptance.BeginRun(runId, ReadPendingInvitation());
    }

    public bool TryArmAcceptance(DadExpectedPartyInviter inviter, out string blocker)
    {
        RequireFrameworkThread();
        return acceptance.TryArm(inviter, out blocker);
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

    public void UpdateAcceptance()
    {
        RequireFrameworkThread();
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

        var revalidated = ReadPendingInvitation(proxy);
        if (revalidated != invitation ||
            !string.Equals(revalidated.InviterName, expected.CharacterName, StringComparison.Ordinal) ||
            revalidated.InviterWorldId != expected.WorldId)
        {
            return;
        }

        bool dispatchResult;
        try
        {
            dispatchResult = proxy->RespondToInvitation(proxy->InviterName.StringPtr, true);
        }
        catch (Exception ex)
        {
            dispatchResult = false;
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

        acceptance.RecordAttempt(invitation, nowUtc);
        log.Information(
            "[dad] Native party acceptance request={RequestId} account={AccountKey} character={CharacterKey} contentId={ContentId} world={WorldId} worker={WorkerSessionId} inviteTime={InviteTime} dispatch={DispatchResult} partyList={PartyListResult}.",
            expected.RunId,
            expected.AccountKey,
            expected.CharacterKey,
            expected.ContentId,
            expected.WorldId,
            expected.WorkerSessionId,
            invitation.InviteTime,
            dispatchResult,
            partyContainsInviter);
    }

    public void Reset()
    {
        inviteAttempts.Clear();
        acceptance.Clear();
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

    private bool PartyListContains(ulong contentId)
        => contentId != 0 && partyList.Any(member => member.ContentId == contentId);

    private static DadPendingPartyInvitation ReadPendingInvitation(InfoProxyPartyInvite* proxy)
        => proxy == null
            ? default
            : new DadPendingPartyInvitation(
                proxy->InviteTime,
                proxy->InviterName.ToString(),
                proxy->InviterWorldId);

    private static DadPendingPartyInvitation ReadPendingInvitation()
        => ReadPendingInvitation(InfoProxyPartyInvite.Instance());

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("InfoProxyPartyInvite may only be accessed on the framework thread.");
    }
}
