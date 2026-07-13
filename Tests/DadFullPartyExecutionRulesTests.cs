using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadFullPartyExecutionRulesTests
{
    [Fact]
    public void ExactVerifiedFourDadPartyWithLocalSlotOneLeaderIsAllowed()
    {
        var (plan, participants) = ValidParty();

        var blockers = DadFullPartyExecutionRules.Evaluate(
            plan,
            DadModuleId.DailyMsq,
            participants,
            4,
            "Daily Roulette");

        Assert.Empty(blockers);
    }

    [Fact]
    public void ExactPartySizeAndVerificationAreRequired()
    {
        var (plan, participants) = ValidParty();
        participants.RemoveAt(3);

        var shortParty = Evaluate(plan, participants);
        Assert.Contains(shortParty, blocker => blocker.Capability == "Participants" && blocker.Severity == DadModuleBlockerSeverity.Failed);

        (plan, participants) = ValidParty();
        participants[2].PostArReady = false;
        var unverified = Evaluate(plan, participants);
        Assert.Contains(unverified, blocker => blocker.Capability == "Participants" && blocker.Summary.Contains("not verified", StringComparison.OrdinalIgnoreCase));

        (plan, participants) = ValidParty();
        participants[3].ActiveCharacterKey = participants[2].ActiveCharacterKey;
        var duplicate = Evaluate(plan, participants);
        Assert.Contains(duplicate, blocker => blocker.Capability == "Participants" && blocker.Summary.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalCoordinatorMustBeTheAuthoritativeSlotOneQueueLeader()
    {
        var (plan, participants) = ValidParty();
        participants[0].AssignedSlotId = "Slot2";
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority" && blocker.Summary.Contains("Slot1", StringComparison.Ordinal));

        (plan, participants) = ValidParty();
        participants[0].IsAuthority = false;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority" && blocker.Summary.Contains("not marked", StringComparison.OrdinalIgnoreCase));

        (plan, participants) = ValidParty();
        participants[1].IsLocalClient = true;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority" && blocker.Summary.Contains("exactly one", StringComparison.OrdinalIgnoreCase));

        (plan, participants) = ValidParty();
        participants[0].Character.Source = DadCharacterSource.PeerRuntime;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority" && blocker.Summary.Contains("loaded on this Dad Coordinator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerDadAndLeaderOrLanPartyQueueAuthorityAreRequired()
    {
        var (plan, participants) = ValidParty();
        plan.Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "LeaderAuthority");

        (plan, participants) = ValidParty();
        plan.Orchestration.QueueAuthority = DadQueueAuthority.LocalOnly;
        Assert.Contains(Evaluate(plan, participants), blocker => blocker.Capability == "QueueAuthority");

        (plan, participants) = ValidParty();
        plan.Orchestration.QueueAuthority = DadQueueAuthority.LanParty;
        Assert.Empty(Evaluate(plan, participants));
    }

    [Fact]
    public void DailyRoulettePlannedLeaderMustBeLocalServerDadCoordinator()
    {
        var request = new DadRunRequest
        {
            DailyMsq = new DadDailyMsqTask(),
            Orchestration = new DadOrchestrationIntent { AuthorityMode = DadAuthorityMode.ServerDad },
        };
        var leader = new DadAcquiredCharacter
        {
            CharacterKey = "Remote Leader@Alpha",
            Source = DadCharacterSource.PeerRuntime,
        };

        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(request, leader, out var remoteBlocker));
        Assert.Contains("loaded on this Dad Coordinator", remoteBlocker, StringComparison.OrdinalIgnoreCase);

        leader.Source = DadCharacterSource.LocalRuntime;
        request.Orchestration.AuthorityMode = DadAuthorityMode.LocalOnly;
        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(request, leader, out var authorityBlocker));
        Assert.Contains("ServerDad", authorityBlocker, StringComparison.Ordinal);

        request.Orchestration.AuthorityMode = DadAuthorityMode.ServerDad;
        Assert.True(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(request, leader, out var allowedBlocker));
        Assert.Empty(allowedBlocker);
    }

    private static IReadOnlyList<DadModuleBlockerDto> Evaluate(
        DadRunPlan plan,
        IReadOnlyList<DadParticipantSnapshot> participants)
        => DadFullPartyExecutionRules.Evaluate(
            plan,
            DadModuleId.DailyMsq,
            participants,
            4,
            "Daily Roulette");

    private static (DadRunPlan Plan, List<DadParticipantSnapshot> Participants) ValidParty()
    {
        var plan = new DadRunPlan
        {
            RequiredParticipantCount = 4,
            LeaderCharacterKey = "Character-1",
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                QueueAuthority = DadQueueAuthority.Leader,
            },
        };
        var participants = Enumerable.Range(1, 4)
            .Select(index => new DadParticipantSnapshot
            {
                ActiveCharacterKey = new DadCharacterKey($"Character-{index}"),
                AssignedSlotId = DadPlannerSlotRules.FormatSlotId(index),
                IsLocalClient = index == 1,
                IsAuthority = index == 1,
                IsAvailable = true,
                IsEligibleForRun = true,
                PostArReady = true,
                State = DadParticipantState.Ready,
                ClaimState = DadClaimState.Granted,
                LeaseState = DadParticipantLeaseState.Granted,
            })
            .ToList();
        return (plan, participants);
    }
}
