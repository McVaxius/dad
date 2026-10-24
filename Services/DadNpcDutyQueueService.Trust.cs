using Dalamud.Game.ClientState.Conditions;
using dad.Models;

namespace dad.Services;

public sealed partial class DadNpcDutyQueueService
{
    private DadTrustProgress? trustRefreshProgress;
    private List<uint> trustRefreshDuties = [];
    private int trustRefreshIndex;
    private int trustRefreshStage;
    private DateTime trustRefreshDeadline;
    private DateTime trustRefreshNextRead;
    private DadTrustProgress? completedTrustRefresh;
    public bool IsTrustRosterRefreshActive { get; private set; }
    public string TrustRosterRefreshSummary { get; private set; } = string.Empty;

    // Called only by an explicit refresh or the existing leveling refresh path.
    // Progress runs on the framework thread; there is no separate collector.
    public bool RequestTrustRosterRefresh(DadAcquiredCharacter owner)
    {
        if (IsTrustRosterRefreshActive) return false;
        completedTrustRefresh = null;
        if (queueOwnership.IsOwned || !CanReadTrustOwner(owner) || !native.MainCommandEnabled(DadNpcDutyQueueMode.Trust))
        {
            TrustRosterRefreshSummary = "Trust refresh needs the exact active character, outside duty and queueing.";
            return false;
        }
        try
        {
            var duties = native.TrustDutyUnlocks();
            var expected = native.DawnCatalog().Where(static duty => duty.Trust && duty.ContentFinderId != 0)
                .Select(static duty => duty.ContentFinderId).ToHashSet();
            if (duties.Count == 0 || duties.Select(static duty => duty.ContentFinderConditionId).Distinct().Count() != duties.Count ||
                !expected.SetEquals(duties.Select(static duty => duty.ContentFinderConditionId)))
            {
                TrustRosterRefreshSummary = "The complete Trust dungeon unlock list is unavailable.";
                return false;
            }
            trustRefreshProgress = new() { AccountKey = new(owner.AccountId), CharacterKey = new(owner.CharacterKey), ContentId = owner.ContentId };
            foreach (var duty in duties.Where(static duty => !duty.Unlocked))
                trustRefreshProgress.Duties.Add(new() { ContentFinderConditionId = duty.ContentFinderConditionId, Unlocked = false, ObservedAtUtc = DadClock.UtcNow });
            trustRefreshDuties = duties.Where(static duty => duty.Unlocked).Select(static duty => duty.ContentFinderConditionId).ToList();
            if (trustRefreshDuties.Count == 0)
            {
                TrustRosterRefreshSummary = "This character has no unlocked Trust dungeons.";
                return false;
            }
            trustRefreshIndex = trustRefreshStage = 0;
            trustRefreshNextRead = DateTime.MinValue;
            trustRefreshDeadline = DadClock.UtcNow.AddMinutes(2);
            IsTrustRosterRefreshActive = true;
            TrustRosterRefreshSummary = "Refreshing this character's Trust roster.";
            return true;
        }
        catch (Exception exception)
        {
            log.Debug(exception, "[dad] Trust roster refresh could not start.");
            CancelTrustRosterRefresh("Trust roster refresh could not read the dungeon catalog.");
            return false;
        }
    }

    public void CancelTrustRosterRefresh(string reason)
    {
        IsTrustRosterRefreshActive = false;
        trustRefreshProgress = null;
        trustRefreshDuties = [];
        completedTrustRefresh = null;
        TrustRosterRefreshSummary = reason;
    }

    public void UpdateTrustRosterRefresh()
    {
        if (!IsTrustRosterRefreshActive || trustRefreshProgress == null) return;
        var progress = trustRefreshProgress;
        var owner = new DadAcquiredCharacter { AccountId = progress.AccountKey.Value, CharacterKey = progress.CharacterKey.Value, ContentId = progress.ContentId };
        if (queueOwnership.IsOwned || !CanReadTrustOwner(owner) || DadClock.UtcNow >= trustRefreshDeadline)
        {
            CancelTrustRosterRefresh("Trust refresh stopped: character/readiness changed, queueing began, or the read timed out.");
            return;
        }
        if (DadClock.UtcNow < trustRefreshNextRead) return;
        try
        {
            var cfc = trustRefreshDuties[trustRefreshIndex];
            if (trustRefreshStage == 0)
            {
                if (!native.MainCommandEnabled(DadNpcDutyQueueMode.Trust))
                {
                    CancelTrustRosterRefresh("Trust refresh stopped because the Trust command became unavailable.");
                    return;
                }
                native.Open(DadNpcDutyQueueMode.Trust, cfc);
                trustRefreshStage = 1;
                trustRefreshNextRead = DadClock.UtcNow.AddMilliseconds(100);
                return;
            }
            if (!native.AgentAvailable(DadNpcDutyQueueMode.Trust) || !native.AddonReady(DadNpcDutyQueueMode.Trust)) return;
            if (native.SelectedTrustDuty()?.ContentFinderConditionId != cfc)
            {
                if (trustRefreshStage == 1) return; // allow the one Open request to settle, without reopening
                CancelTrustRosterRefresh("Trust refresh stopped because its dungeon selection changed.");
                return;
            }
            if (trustRefreshStage == 1)
            {
                native.UpdateTrustAddon();
                trustRefreshStage = 2;
                trustRefreshNextRead = DadClock.UtcNow.AddMilliseconds(100);
                return;
            }
            var count = native.TrustMemberCount;
            if (!native.TrustMembersAvailable || count is <= 0 or > 12) return;
            // Require every advertised member, including locked entries, to be initialized.
            // Do not let an unknown identity disappear from a supposedly complete roster.
            for (byte index = 0; index < count; index++)
            {
                var member = native.TrustMember(index);
                if (!member.Initialized) return;
                if (!DadTrustNpcCatalog.TryResolve(member.MemberId, member.ClassJobId, out _) ||
                    member.Roles == DadTrustRoles.None || (member.Roles & ~DadTrustRoles.All) != 0 || member.Unlocked && member.Level <= 0)
                {
                    CancelTrustRosterRefresh("Trust refresh found an unsupported or unread companion.");
                    return;
                }
            }
            var reading = CaptureSelectedTrustProgress(owner);
            if (reading == null || reading.Npcs.Count != count || reading.Npcs.Select(static npc => npc.NpcId).Distinct().Count() != count ||
                reading.Duties.SingleOrDefault()?.ContentFinderConditionId != cfc || reading.Duties[0].Unlocked != true)
            {
                CancelTrustRosterRefresh("Trust refresh could not verify the current dungeon's complete member data.");
                return;
            }
            var merged = DadTrustProgressRules.Merge(progress, reading, progress.AccountKey, progress.CharacterKey, progress.ContentId);
            if (merged == null || reading.Npcs.Any(npc => !merged.Npcs.Any(saved => saved.NpcId == npc.NpcId)))
            { CancelTrustRosterRefresh("Trust refresh lost character ownership or found conflicting readings."); return; }
            trustRefreshProgress = merged;
            trustRefreshIndex++;
            if (trustRefreshIndex < trustRefreshDuties.Count)
            {
                trustRefreshStage = 0;
                TrustRosterRefreshSummary = $"Read {trustRefreshIndex}/{trustRefreshDuties.Count} Trust dungeons.";
                return;
            }
            merged.RosterObservedAtUtc = DadClock.UtcNow;
            completedTrustRefresh = merged;
            IsTrustRosterRefreshActive = false;
            trustRefreshProgress = null;
            TrustRosterRefreshSummary = "Trust roster refreshed.";
        }
        catch (Exception exception)
        {
            log.Debug(exception, "[dad] Trust roster refresh failed.");
            CancelTrustRosterRefresh("Trust roster refresh failed; no complete roster was recorded.");
        }
    }

    public DadTrustProgress? CaptureTrustProgress(DadAcquiredCharacter owner)
    {
        var completed = DadTrustProgressRules.ForOwner(completedTrustRefresh, new(owner.AccountId), new(owner.CharacterKey), owner.ContentId);
        return DadTrustProgressRules.Merge(completed, CaptureSelectedTrustProgress(owner), new(owner.AccountId), new(owner.CharacterKey), owner.ContentId);
    }

    private bool CanReadTrustOwner(DadAcquiredCharacter owner)
        => owner.ContentId != 0 && owner.ContentId == game.ContentId && !string.IsNullOrWhiteSpace(owner.AccountId) &&
           !string.IsNullOrWhiteSpace(owner.CharacterKey) && game.IsLoggedIn && game.HasLocalPlayer &&
           !game.Condition(ConditionFlag.BetweenAreas) && !game.Condition(ConditionFlag.BetweenAreas51) &&
           !game.Condition(ConditionFlag.BoundByDuty) && !game.Condition(ConditionFlag.InDutyQueue);
}
