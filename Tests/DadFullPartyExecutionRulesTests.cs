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

    [Fact]
    public void RelaxedCoordinatorIdentityRequiresLaunchIfOfflineAndTheSameAccount()
    {
        var request = new DadRunRequest
        {
            DailyMsq = new DadDailyMsqTask(),
            Orchestration = new DadOrchestrationIntent
            {
                AuthorityMode = DadAuthorityMode.ServerDad,
                QueueAuthority = DadQueueAuthority.Leader,
                PreferredLeaderCharacterKey = new DadCharacterKey("Requested Leader@World"),
                RequiredRosterCharacters =
                [
                    new DadRosterCharacterRef
                    {
                        AccountKey = new DadAccountKey("coordinator-account"),
                        CharacterKey = new DadCharacterKey("Requested Leader@World"),
                    },
                ],
            },
        };
        var requestedLeader = Character("Requested Leader@World", "coordinator-account", DadCharacterSource.XadbOnly);
        var wrongCharacterSameAccount = Character("Other Character@World", "coordinator-account", DadCharacterSource.LocalRuntime);

        Assert.True(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            requestedLeader,
            new DadAccountKey("coordinator-account"),
            wrongCharacterSameAccount,
            requireExactLocalIdentity: false,
            allowWakeableCoordinatorLeader: true,
            out var wakeableBlocker));
        Assert.Empty(wakeableBlocker);

        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            requestedLeader,
            new DadAccountKey("coordinator-account"),
            wrongCharacterSameAccount,
            requireExactLocalIdentity: false,
            allowWakeableCoordinatorLeader: false,
            out var policyBlocker));
        Assert.Contains("exact character loaded", policyBlocker, StringComparison.OrdinalIgnoreCase);

        var wrongAccount = Character("Other Character@World", "different-account", DadCharacterSource.LocalRuntime);
        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            requestedLeader,
            new DadAccountKey("coordinator-account"),
            wrongAccount,
            requireExactLocalIdentity: false,
            allowWakeableCoordinatorLeader: true,
            out var accountBlocker));
        Assert.Contains("different account", accountBlocker, StringComparison.OrdinalIgnoreCase);

        Assert.False(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            requestedLeader,
            new DadAccountKey("coordinator-account"),
            wrongCharacterSameAccount,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: true,
            out var strictBlocker));
        Assert.Contains("exact character loaded", strictBlocker, StringComparison.OrdinalIgnoreCase);

        var exactLocal = Character("Requested Leader@World", "coordinator-account", DadCharacterSource.LocalRuntime);
        Assert.True(DadFullPartyExecutionRules.TryValidatePlannedCoordinatorLeader(
            request,
            exactLocal,
            new DadAccountKey("coordinator-account"),
            exactLocal,
            requireExactLocalIdentity: true,
            allowWakeableCoordinatorLeader: false,
            out var exactBlocker));
        Assert.Empty(exactBlocker);
    }

    [Fact]
    public void UnfilteredLocalRuntimeIdentityWinsOverPresentationFilteredCharacters()
    {
        var projectedCharacters = new[]
        {
            Character("Requested Leader@World", "coordinator-account", DadCharacterSource.XadbOnly),
        };
        var hiddenRuntime = Character("Hidden Active@World", "different-account", DadCharacterSource.LocalRuntime);

        var resolved = DadFullPartyExecutionRules.ResolveActiveCoordinatorCharacter(
            hiddenRuntime,
            projectedCharacters);

        Assert.Same(hiddenRuntime, resolved);
        Assert.Equal("different-account", resolved!.AccountId);
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

    private static DadAcquiredCharacter Character(
        string characterKey,
        string accountId,
        DadCharacterSource source)
        => new()
        {
            CharacterKey = characterKey,
            AccountId = accountId,
            Source = source,
        };

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
