using dad.Models;
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
}
