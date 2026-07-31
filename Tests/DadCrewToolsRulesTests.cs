using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCrewToolsRulesTests
{
    [Fact]
    public void Classify_UsesDutyFinderQueueSizeForAlliance()
    {
        var result = DadCrewToolsRules.Classify(
            DadPlannerActivityMode.DutyPremade,
            selectedDutyQueueSize: 24,
            expectedPartySize: 4);

        Assert.True(result.CanCreate);
        Assert.Equal(DadCrewFormationMode.AlliancePartyFinder, result.Mode);
    }

    [Theory]
    [InlineData(DadPlannerActivityMode.DutySupport)]
    [InlineData(DadPlannerActivityMode.Trust)]
    [InlineData(DadPlannerActivityMode.DutySupportLeveling)]
    [InlineData(DadPlannerActivityMode.TrustLeveling)]
    [InlineData(DadPlannerActivityMode.Squadron)]
    public void Classify_RejectsNpcOnlyRegularGroups(DadPlannerActivityMode activityMode)
    {
        var result = DadCrewToolsRules.Classify(activityMode, selectedDutyQueueSize: 4, expectedPartySize: 4);

        Assert.False(result.CanCreate);
        Assert.Equal(DadCrewFormationMode.Unavailable, result.Mode);
        Assert.Contains("NPC-only", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_RejectsSoloRegularGroups()
    {
        var result = DadCrewToolsRules.Classify(
            DadPlannerActivityMode.LocalDuty,
            selectedDutyQueueSize: 1,
            expectedPartySize: 1);

        Assert.False(result.CanCreate);
        Assert.Equal(DadCrewFormationMode.Unavailable, result.Mode);
    }

    [Fact]
    public void Classify_RejectsSoloRosterEvenWhenDutyCatalogIsAllianceSized()
    {
        var result = DadCrewToolsRules.Classify(
            DadPlannerActivityMode.DutyPremade,
            selectedDutyQueueSize: 24,
            expectedPartySize: 1);

        Assert.False(result.CanCreate);
        Assert.Equal(DadCrewFormationMode.Unavailable, result.Mode);
    }

    [Fact]
    public void FormationAdmission_IgnoresOnlyRunBlockersAndKeepsOrderedStructuralAndWakeFailures()
    {
        string[] orderedStatic = ["missing identity", "missing roulette", "invalid requested job", "stop already met"];
        string[] runOnly = ["missing roulette", "stop already met"];
        string[] readiness = ["offline"];
        string[] wakePolicy = ["Already online cannot wake"];

        var formation = DadCrewToolsRules.EvaluateFormationAdmission(
            formationOnly: true,
            orderedStatic,
            readiness,
            wakePolicy,
            runOnly);
        var ordinary = DadCrewToolsRules.EvaluateFormationAdmission(
            formationOnly: false,
            orderedStatic,
            readiness,
            wakePolicy,
            runOnly);

        Assert.Equal(["missing identity", "invalid requested job"], formation.StaticBlockers);
        Assert.Equal(["offline"], formation.ReadinessBlockers);
        Assert.Equal(["Already online cannot wake"], formation.ScheduleBlockers);
        Assert.False(formation.CanSchedule);
        Assert.Equal(orderedStatic, ordinary.StaticBlockers);
        Assert.False(ordinary.CanSchedule);
    }

    [Fact]
    public void FormationAdmission_TreatsReadinessAsSchedulableWait()
    {
        var admission = DadCrewToolsRules.EvaluateFormationAdmission(
            formationOnly: true,
            orderedStaticBlockers: ["missing duty"],
            readinessBlockers: ["offline", "waiting for post-AR"],
            scheduleBlockers: [],
            runOnlyBlockers: ["missing duty"]);

        Assert.True(admission.CanSchedule);
        Assert.False(admission.CanStart);
        Assert.Empty(admission.StaticBlockers);
        Assert.Equal(["offline", "waiting for post-AR"], admission.ReadinessBlockers);
    }

    [Fact]
    public void CrewPartyFinderAuthorization_RequiresExactActiveRun()
    {
        Assert.True(DadCrewToolsRules.IsExactCrewPartyFinderContext(
            DadAlliancePartyFinderActionContext.CrewFormation("crew-1"),
            "crew-1"));
        Assert.False(DadCrewToolsRules.IsExactCrewPartyFinderContext(
            DadAlliancePartyFinderActionContext.CrewFormation("crew-2"),
            "crew-1"));
        Assert.False(DadCrewToolsRules.IsExactCrewPartyFinderContext(
            DadAlliancePartyFinderActionContext.Debug,
            "crew-1"));
    }

    [Fact]
    public void FormationStatus_StaysActiveAtRegularGroupReady()
    {
        var status = new DadCrewFormationStatus { Phase = DadCrewFormationPhase.RegularGroupReady };

        Assert.True(status.IsActive);

        status.Phase = DadCrewFormationPhase.Completed;
        Assert.False(status.IsActive);
    }

    [Fact]
    public void RegularGroupReady_RequiresExactHeldRun()
    {
        var run = new DadRunResult
        {
            RequestId = "request-1",
            Status = DadRunStatus.Running,
            Phase = DadRunPhase.GroupReady,
        };

        Assert.True(DadCrewToolsRules.IsExactRegularGroupReady(run, "request-1"));
        Assert.False(DadCrewToolsRules.IsExactRegularGroupReady(run, "request-2"));
    }

    [Fact]
    public void AllianceSequence_GrabsOnceAndCompletesOnlyAfterExactCleanup()
    {
        var listing = new DadAlliancePartyFinderStatus
        {
            RecruitmentId = "recruitment-1",
            State = DadAllianceRecruitmentState.ListingOpen,
            OwnsRecruitment = true,
        };
        Assert.True(DadCrewToolsRules.ShouldGrabExactAlliance(
            listing,
            "recruitment-1",
            grabAlreadyRequested: false));
        Assert.False(DadCrewToolsRules.ShouldGrabExactAlliance(
            listing,
            "recruitment-1",
            grabAlreadyRequested: true));
        Assert.False(DadCrewToolsRules.ShouldGrabExactAlliance(
            listing,
            "recruitment-2",
            grabAlreadyRequested: false));

        listing.State = DadAllianceRecruitmentState.Complete;
        Assert.False(DadCrewToolsRules.IsExactAllianceComplete(listing, "recruitment-1"));
        listing.OwnsRecruitment = false;
        Assert.True(DadCrewToolsRules.IsExactAllianceComplete(listing, "recruitment-1"));
        Assert.False(DadCrewToolsRules.IsExactAllianceComplete(listing, "recruitment-2"));
    }

    [Fact]
    public void Disband_RequiresStableAuthoritativeLocalLeadership()
    {
        var ready = DadCrewToolsRules.EvaluateDisband(
            localContentId: 10,
            leaderContentId: 10,
            memberContentIds: [10, 20],
            isCrossRealmParty: true,
            isInDuty: false,
            isQueued: false,
            isWorldStable: true);
        Assert.True(ready.CanDisband);
        Assert.Equal([10UL, 20UL], ready.MemberContentIds);

        Assert.False(DadCrewToolsRules.EvaluateDisband(
            localContentId: 10,
            leaderContentId: 20,
            memberContentIds: [10, 20],
            isCrossRealmParty: false,
            isInDuty: false,
            isQueued: false,
            isWorldStable: true).CanDisband);

        Assert.False(DadCrewToolsRules.EvaluateDisband(
            localContentId: 10,
            leaderContentId: 10,
            memberContentIds: [10, 20],
            isCrossRealmParty: false,
            isInDuty: true,
            isQueued: false,
            isWorldStable: true).CanDisband);
    }
}
