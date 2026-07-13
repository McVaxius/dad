using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadSchedulerRoutingRulesTests
{
    private const string AccountW = "dad-client-01c4df9f09b5488abc6980d9c09f103e";
    private const string AccountX = "dad-client-42a9d8e48b3a411689c692ada8e3676f";

    [Fact]
    public void ConfiguredAccountIdentityExistsBeforeLoginAndDoesNotFollowCharacterLifecycle()
    {
        var beforeLogin = DadSchedulerRoutingRules.ResolveStableClientAccount($"  {AccountX}  ");
        var duringLogin = DadSchedulerRoutingRules.ResolveStableClientAccount(AccountX);
        var afterLogout = DadSchedulerRoutingRules.ResolveStableClientAccount(AccountX);
        var duringRelog = DadSchedulerRoutingRules.ResolveStableClientAccount(AccountX);

        Assert.Equal(AccountX, beforeLogin.Value);
        Assert.Equal(beforeLogin, duringLogin);
        Assert.Equal(beforeLogin, afterLogout);
        Assert.Equal(beforeLogin, duringRelog);
    }

    [Fact]
    public void ConnectedCharacterSelectClientIsRoutableWithoutAvailabilityOrCharacterSnapshot()
    {
        var x = Participant("worker-x", AccountX, isAvailable: false, activeCharacter: string.Empty);

        var resolved = DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [x],
            worker => worker.Value == "worker-x");

        Assert.Same(x, resolved);
        Assert.False(resolved!.IsAvailable);
        Assert.True(resolved.ActiveCharacterKey.IsEmpty);
    }

    [Fact]
    public void StaleAndPhysicallyDisconnectedClientsAreRejected()
    {
        var stale = Participant("worker-stale", AccountX, isAvailable: true, activeCharacter: "Hard'carry Gray'parse@Excalibur");
        stale.State = DadParticipantState.Stale;
        var disconnected = Participant("worker-disconnected", AccountX, isAvailable: true, activeCharacter: "Hard'carry Gray'parse@Excalibur");

        Assert.Null(DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [stale],
            static _ => true));
        Assert.Null(DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [disconnected],
            static _ => false));
    }

    [Fact]
    public void ExactStableAccountRoutesSlot2ToXWithoutAliasesOrActiveCharacterEvidence()
    {
        var aliasProjection = Participant("worker-alias", "cached-account", isAvailable: true, activeCharacter: "Hard'carry Gray'parse@Excalibur");
        aliasProjection.ManagedAccountAlias = AccountX;
        aliasProjection.Character.AccountId = AccountX;
        var x = Participant("worker-x", AccountX, isAvailable: false, string.Empty);

        var resolved = DadSchedulerRoutingRules.ResolveExactConnectedClient(
            new DadAccountKey(AccountX),
            [aliasProjection, x],
            static _ => true);

        Assert.NotNull(resolved);
        Assert.Equal("worker-x", resolved!.WorkerSessionId.Value);
    }

    [Fact]
    public void MissingSecondClientKeepsPreArmDispatchAtZero()
    {
        var slots = Slots();
        var participants = new[] { Participant("worker-w", AccountW, true, "Venat Azem@Excalibur") };
        var actions = new List<string>();

        var allResolved = DadSchedulerRoutingRules.TryResolveAllTakeoverClients(
            slots,
            participants,
            static _ => true,
            out var routes);
        if (allResolved)
        {
            actions.AddRange(routes.Select(static route => $"Prepare:{route.SlotId}"));
            actions.Add("AcquireSuppression");
            actions.Add("Reset");
            actions.Add("Relog");
        }

        Assert.False(allResolved);
        Assert.Empty(routes);
        Assert.Empty(actions);
    }

    [Fact]
    public void BothConnectedClientsReceiveOnePreArmDispatchEach()
    {
        var slots = Slots();
        var participants = new[]
        {
            Participant("worker-w", AccountW, true, "Venat Azem@Excalibur"),
            Participant("worker-x", AccountX, false, string.Empty),
        };

        var allResolved = DadSchedulerRoutingRules.TryResolveAllTakeoverClients(
            slots,
            participants,
            static _ => true,
            out var routes);

        Assert.True(allResolved);
        Assert.Equal(["Slot1", "Slot2"], routes.Select(static route => route.SlotId).ToArray());
        Assert.Equal(2, routes.Select(static route => route.Participant.WorkerSessionId.Value).Distinct().Count());
        var prepareDispatches = routes.Select(static route => $"Prepare:{route.SlotId}").ToList();
        Assert.Equal(1, prepareDispatches.Count(static action => action == "Prepare:Slot1"));
        Assert.Equal(1, prepareDispatches.Count(static action => action == "Prepare:Slot2"));
    }

    [Fact]
    public void ConnectedCorrectCharacterCanAdvanceToReadyInsteadOfWaitingForClient()
    {
        var decision = DadWakePolicyRules.Evaluate(
            DadSchedulerWakePolicy.LaunchIfOffline,
            sameAccountClientConnected: true,
            correctCharacter: true,
            postArReady: true,
            takeoverStatus: DadWakeTakeoverStatus.Ready);

        Assert.True(decision.Ready);
        Assert.Equal(DadWakeTakeoverStage.Ready, decision.Stage);
        Assert.DoesNotContain("Waiting", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchedulerSlotClonePreservesSeparateClientAndCharacterStates()
    {
        var slot = new DadSchedulerSlotState
        {
            ClientConnected = true,
            IsOnline = false,
            MatchedWorkerSessionId = new DadWorkerSessionId("worker-x"),
        };

        var clone = slot.Clone();

        Assert.True(clone.ClientConnected);
        Assert.False(clone.IsOnline);
        Assert.Equal("worker-x", clone.MatchedWorkerSessionId.Value);
    }

    private static List<DadSchedulerSlotState> Slots()
        =>
        [
            new DadSchedulerSlotState
            {
                SlotId = "Slot1",
                RequiredAccountKey = new DadAccountKey(AccountW),
                RequiredCharacterKey = new DadCharacterKey("Venat Azem@Excalibur"),
            },
            new DadSchedulerSlotState
            {
                SlotId = "Slot2",
                RequiredAccountKey = new DadAccountKey(AccountX),
                RequiredCharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            },
        ];

    private static DadParticipantSnapshot Participant(
        string worker,
        string stableAccount,
        bool isAvailable,
        string activeCharacter)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(stableAccount),
            IsAvailable = isAvailable,
            IsEligibleForRun = true,
            State = DadParticipantState.Idle,
            ActiveCharacterKey = new DadCharacterKey(activeCharacter),
            Character = new DadAcquiredCharacter
            {
                AccountId = stableAccount,
                CharacterKey = activeCharacter,
            },
        };
}
