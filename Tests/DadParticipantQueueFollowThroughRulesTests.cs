using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadParticipantQueueFollowThroughRulesTests
{
    [Fact]
    public void MultiplayerPremadeAndLanDutyUseObserveAcceptOnlyFollowThrough()
    {
        var premadePlan = Plan(DadModuleId.PremadeDuty);
        var lanPlan = Plan(DadModuleId.Duty);
        lanPlan.Request.Dungeon = new DadDungeonTask { QueueViaLanParty = true };

        Assert.True(DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(premadePlan, premadePlan.Modules[0]));
        Assert.True(DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(lanPlan, lanPlan.Modules[0]));

        var customPlan = Plan(DadModuleId.CustomDuty);
        customPlan.Request.CustomDuty = new DadCustomDutyTask { ExpectedPartySize = 2 };
        var commendationPlan = Plan(DadModuleId.Commendation);
        commendationPlan.Request.Commendation = new DadCommendationTask();

        Assert.True(DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(customPlan, customPlan.Modules[0]));
        Assert.True(DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(commendationPlan, commendationPlan.Modules[0]));
    }

    [Fact]
    public void MogtomeRetainsItsSpecializedParticipantExecution()
    {
        var plan = Plan(DadModuleId.Mogtome);

        Assert.False(DadParticipantQueueFollowThroughRules.IsObserveAcceptOnlyLane(plan, plan.Modules[0]));
    }

    [Fact]
    public void ParticipantMayAcceptCommenceButCannotDriveDutyFinderRegistration()
    {
        Assert.True(DadParticipantQueueFollowThroughRules.IsAllowed(DadParticipantQueueAction.ObserveQueueAndAreaTruth));
        Assert.True(DadParticipantQueueFollowThroughRules.IsAllowed(DadParticipantQueueAction.AcceptCommence));
        Assert.False(DadParticipantQueueFollowThroughRules.IsAllowed(DadParticipantQueueAction.OpenDutyFinder));
        Assert.False(DadParticipantQueueFollowThroughRules.IsAllowed(DadParticipantQueueAction.SelectDuty));
        Assert.False(DadParticipantQueueFollowThroughRules.IsAllowed(DadParticipantQueueAction.RegisterDuty));
        Assert.False(DadParticipantQueueFollowThroughRules.IsAllowed(DadParticipantQueueAction.AlterSyncSettings));
    }

    private static DadRunPlan Plan(DadModuleId moduleId)
        => new()
        {
            RequiredParticipantCount = 2,
            RequiresRemoteParticipants = true,
            Request = new DadRunRequest(),
            Modules =
            [
                new DadPlannedModuleExecution
                {
                    ModuleId = moduleId,
                    ExpectedPartySize = 2,
                    RequiresPeers = true,
                },
            ],
        };
}
