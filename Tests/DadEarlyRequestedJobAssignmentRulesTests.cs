using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadEarlyRequestedJobAssignmentRulesTests
{
    [Fact]
    public void FrozenAssignmentsAreBuiltBeforeAnyCharacterLoadsWithExactP1177Jobs()
    {
        uint[] jobs = [40, 32, 24, 38];
        var slots = jobs.Select((job, index) => new DadSchedulerSlotState
        {
            SlotId = $"Slot{index + 1}",
            RequiredAccountKey = new DadAccountKey($"account-{index + 1}"),
            RequiredCharacterKey = new DadCharacterKey($"character-{index + 1}@world"),
            RequiredJobId = job,
        }).ToList();
        var request = new DadRunRequest
        {
            RequestId = "frozen-request",
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                ModuleTarget = DadModuleId.DailyMsq,
                RequiredRosterCharacters = slots.Select((slot, index) => new DadRosterCharacterRef
                {
                    AccountKey = slot.RequiredAccountKey,
                    CharacterKey = slot.RequiredCharacterKey,
                    ContentId = (ulong)(1001 + index),
                    RequiredJobId = slot.RequiredJobId,
                }).ToList(),
            },
        };

        var assignments = DadEarlyRequestedJobAssignmentRules.Build(
            request,
            slots,
            new DadWorkerSessionId("authority"));

        Assert.Equal(jobs.Cast<uint?>(), assignments.Select(static assignment => assignment.RequiredJobId));
        Assert.Equal(new ulong[] { 1001, 1002, 1003, 1004 }, assignments.Select(static assignment => assignment.RequiredContentId));
        Assert.All(assignments, assignment =>
        {
            Assert.Equal("frozen-request", assignment.RunId);
            Assert.True(assignment.RequirePostArReady);
        });

        request.RequestId = "changed-after-admission";
        request.Orchestration.RequiredRosterCharacters[0].ContentId = 9999;
        slots[0].RequiredJobId = 19;
        Assert.Equal("frozen-request", assignments[0].RunId);
        Assert.Equal(1001ul, assignments[0].RequiredContentId);
        Assert.Equal(40u, assignments[0].RequiredJobId);
    }

    [Fact]
    public void MixedLanAndRegisteredIslandSlotsAssignOnlyLanSlotOneAndReachAutoPartyAuthorization()
    {
        var proposalId = Guid.Parse("f5c4e83d-0d7b-45f0-8f32-4d8680e59a84");
        var slots = new List<DadSchedulerSlotState>
        {
            new()
            {
                SlotId = "Slot1",
                RequiredAccountKey = new DadAccountKey("account-1"),
                RequiredCharacterKey = new DadCharacterKey("character-1@world"),
                RequiredJobId = 19,
            },
            new()
            {
                SlotId = "Slot2",
                IsRegisteredIsland = true,
                SharedIdentityToken = "shared-registered-slot-2",
                RequiredJobId = 24,
            },
        };
        var request = new DadRunRequest
        {
            RequestId = "mixed-request",
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                ModuleTarget = DadModuleId.PremadeDuty,
                AutoPartyProposalId = proposalId.ToString("D"),
                RequiredRosterCharacters =
                [
                    new DadRosterCharacterRef
                    {
                        AccountKey = slots[0].RequiredAccountKey,
                        CharacterKey = slots[0].RequiredCharacterKey,
                        ContentId = 1001,
                        RequiredJobId = 19,
                    },
                    new DadRosterCharacterRef
                    {
                        SharedIdentityToken = slots[1].SharedIdentityToken,
                        RequiredJobId = 24,
                    },
                ],
            },
        };

        var assignment = Assert.Single(DadEarlyRequestedJobAssignmentRules.Build(
            request,
            slots,
            new DadWorkerSessionId("authority")));

        Assert.Equal("Slot1", assignment.AssignedSlotId);
        Assert.Equal(19u, assignment.RequiredJobId);
        var authorization = DadAutoPartySchedulerAuthorizationRules.Evaluate(
            request,
            candidate => new(DadAutoPartyAuthorizationState.Authorized, "dad-autoparty-authorized", candidate));
        Assert.Equal(DadAutoPartyAuthorizationState.Authorized, authorization.State);
        Assert.Equal(proposalId, authorization.ProposalId);
        var clone = slots[1].Clone();
        Assert.True(clone.IsRegisteredIsland);
        Assert.Equal("shared-registered-slot-2", clone.SharedIdentityToken);
    }
}
