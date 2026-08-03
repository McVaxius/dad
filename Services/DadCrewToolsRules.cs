using dad.Models;

namespace dad.Services;

internal sealed record DadCrewFormationClassification(
    DadCrewFormationMode Mode,
    string Summary,
    string BlockedReason)
{
    public bool CanCreate => Mode != DadCrewFormationMode.Unavailable &&
                             string.IsNullOrWhiteSpace(BlockedReason);
}

internal static class DadCrewToolsRules
{
    public static DadCrewFormationClassification Classify(
        DadPlannerActivityMode activityMode,
        int allianceACount,
        int allianceBCount,
        int allianceCCount,
        int expectedPartySize)
    {
        if (IsNpcOnly(activityMode))
        {
            const string blocker = "Crew Tools does not form parties for solo or NPC-only presets.";
            return new DadCrewFormationClassification(
                DadCrewFormationMode.Unavailable,
                "NPC-only",
                blocker);
        }

        if (expectedPartySize < 2)
        {
            const string blocker = "Crew Tools requires an effective party size of at least two.";
            return new DadCrewFormationClassification(
                DadCrewFormationMode.Unavailable,
                "Solo",
                blocker);
        }

        if (allianceACount > 0 && allianceBCount > 0 && allianceCCount > 0)
        {
            return new DadCrewFormationClassification(
                DadCrewFormationMode.AlliancePartyFinder,
                "Alliance Party Finder (A/B/C populated)",
                string.Empty);
        }

        return new DadCrewFormationClassification(
            DadCrewFormationMode.RegularParty,
            $"Regular party ({expectedPartySize} players)",
            string.Empty);
    }

    public static DadPlannerValidationDetails EvaluateFormationAdmission(
        bool formationOnly,
        IEnumerable<string> orderedStaticBlockers,
        IEnumerable<string> readinessBlockers,
        IEnumerable<string> scheduleBlockers,
        IEnumerable<string> runOnlyBlockers)
    {
        var staticBlockers = orderedStaticBlockers.ToList();
        if (formationOnly)
        {
            var ignored = runOnlyBlockers
                .Where(static blocker => !string.IsNullOrWhiteSpace(blocker))
                .Select(static blocker => blocker.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            staticBlockers.RemoveAll(blocker => ignored.Contains(blocker.Trim()));
        }

        return DadPlannerValidationRules.Evaluate(
            staticBlockers,
            readinessBlockers,
            scheduleBlockers);
    }

    public static DadPlannerGroup BuildRuntimeFormationGroup(DadPlannerGroup source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var runtime = DadSchedulerGroupCloneRules.CloneWithSlots(source, source.Slots);
        runtime.AutoPartyFormationOnly = true;
        foreach (var row in runtime.Slots)
        {
            row.LevelSeekTarget = null;
            row.SkipIfDailyRouletteRewardReceived = false;
        }

        return runtime;
    }

    public static bool IsExactCrewPartyFinderContext(
        DadAlliancePartyFinderActionContext context,
        string activeCrewFormationRunId)
        => context.Source == DadAlliancePartyFinderActionSource.CrewFormation &&
           !string.IsNullOrWhiteSpace(context.CrewFormationRunId) &&
           string.Equals(
               context.CrewFormationRunId,
               activeCrewFormationRunId,
               StringComparison.Ordinal);

    public static bool IsExactRegularGroupReady(
        DadRunResult? run,
        string requestId)
        => run != null &&
           !string.IsNullOrWhiteSpace(requestId) &&
           string.Equals(run.RequestId, requestId, StringComparison.Ordinal) &&
           run.Status == DadRunStatus.Running &&
           run.Phase == DadRunPhase.GroupReady;

    public static bool ShouldGrabExactAlliance(
        DadAlliancePartyFinderStatus? status,
        string recruitmentId,
        bool grabAlreadyRequested)
        => !grabAlreadyRequested &&
           status != null &&
           !string.IsNullOrWhiteSpace(recruitmentId) &&
           string.Equals(status.RecruitmentId, recruitmentId, StringComparison.Ordinal) &&
           status.State == DadAllianceRecruitmentState.ListingOpen &&
           status.OwnsRecruitment;

    public static bool IsExactAllianceComplete(
        DadAlliancePartyFinderStatus? status,
        string recruitmentId)
        => status != null &&
           !string.IsNullOrWhiteSpace(recruitmentId) &&
           string.Equals(status.RecruitmentId, recruitmentId, StringComparison.Ordinal) &&
           status.State == DadAllianceRecruitmentState.Complete &&
           status.OwnsRecruitment;

    public static DadPartyDisbandPreflight EvaluateDisband(
        ulong localContentId,
        ulong leaderContentId,
        IEnumerable<ulong>? memberContentIds,
        bool isCrossRealmParty,
        bool isInDuty,
        bool isQueued,
        bool isWorldStable,
        string leaderName = "")
    {
        var members = (memberContentIds ?? [])
            .Where(static id => id != 0)
            .Distinct()
            .Order()
            .ToList();
        var blocker = string.Empty;
        if (isInDuty || isQueued)
            blocker = "Disband is available only while out of duty and out of the Duty Finder queue.";
        else if (!isWorldStable)
            blocker = "Wait for a stable, non-occupied world state before disbanding.";
        else if (localContentId == 0)
            blocker = "The local character identity is unavailable.";
        else if (members.Count < 2)
            blocker = "No party with at least two authoritative members is available to disband.";
        else if (!members.Contains(localContentId))
            blocker = "The authoritative party membership does not contain the local character.";
        else if (leaderContentId == 0 || leaderContentId != localContentId)
            blocker = "The local character is not the authoritative party leader.";

        return new DadPartyDisbandPreflight
        {
            CanDisband = string.IsNullOrWhiteSpace(blocker),
            LocalContentId = localContentId,
            LeaderContentId = leaderContentId,
            IsCrossRealmParty = isCrossRealmParty,
            IsInDuty = isInDuty,
            IsQueued = isQueued,
            IsWorldStable = isWorldStable,
            MemberContentIds = members,
            LeaderName = leaderName?.Trim() ?? string.Empty,
            BlockedReason = blocker,
            Summary = string.IsNullOrWhiteSpace(blocker)
                ? $"Ready to disband the exact {members.Count}-member party."
                : blocker,
        };
    }

    private static bool IsNpcOnly(DadPlannerActivityMode activityMode)
        => activityMode is DadPlannerActivityMode.DutySupport
            or DadPlannerActivityMode.Trust
            or DadPlannerActivityMode.DutySupportLeveling
            or DadPlannerActivityMode.TrustLeveling
            or DadPlannerActivityMode.Squadron;
}
