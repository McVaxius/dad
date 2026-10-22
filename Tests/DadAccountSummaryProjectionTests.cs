using dad.Models;
using dad.Windows;
using Xunit;

namespace dad.Tests;

public sealed class DadAccountSummaryProjectionTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DirectLocalWinsItsSessionAndNewestRemoteObservationWinsOtherSessions()
    {
        var local = Participant("local", "account-a", 10);
        var transportLocal = Participant("LOCAL", "account-a", 0);
        transportLocal.State = DadParticipantState.Failed;
        var remoteOld = Participant("remote", "account-b", 20);
        var remoteNew = Participant("REMOTE", "account-b", 1);
        remoteNew.State = DadParticipantState.Running;

        var projection = DadAccountSummaryProjection.Build(local, [transportLocal, remoteNew, remoteOld], Now);

        Assert.Equal(2, projection.Accounts.Count);
        Assert.Same(local, projection.Accounts.Single(row => row.AccountKey.Value == "account-a").Participant);
        Assert.Same(remoteNew, projection.Accounts.Single(row => row.AccountKey.Value == "account-b").Participant);
        Assert.All(projection.Accounts, row => Assert.False(row.HasMultipleLiveSessions));
        Assert.Equal(DadParticipantState.Failed, transportLocal.State);
        Assert.Equal(DadParticipantState.Ready, remoteOld.State);
    }

    [Fact]
    public void NormalizedAccountIdentityGroupsConflictingSessionsWithoutUsingAliasOrCharacterName()
    {
        var local = Participant("local", " account-a ");
        var otherSession = Participant("other", "ACCOUNT-A");
        otherSession.ActiveCharacterKey = new("another-character");
        otherSession.State = DadParticipantState.Failed;
        var differentAccount = Participant("third", "account-b");

        var projection = DadAccountSummaryProjection.Build(local, [local, otherSession, differentAccount], Now);

        Assert.Equal(2, projection.Accounts.Count);
        var conflict = Assert.Single(projection.Accounts, row => row.HasMultipleLiveSessions);
        Assert.True(DadRosterIdentity.SameAccount(local.ManagedAccountKey, conflict.AccountKey));
        Assert.False(projection.Accounts.Single(row => row.AccountKey.Value == "account-b").HasMultipleLiveSessions);
        Assert.Equal(local.ManagedAccountAlias, differentAccount.ManagedAccountAlias);
        Assert.Equal(local.ActiveCharacterKey, differentAccount.ActiveCharacterKey);
    }

    [Theory]
    [InlineData(DadParticipantState.Ready, 30, true)]
    [InlineData(DadParticipantState.Ready, 31, false)]
    [InlineData(DadParticipantState.Stale, 0, false)]
    public void OnlyDistinctLiveSessionsProduceConflict(DadParticipantState state, int ageSeconds, bool conflict)
    {
        var local = Participant("local", "account-a");
        var remote = Participant("remote", "account-a", ageSeconds);
        remote.State = state;

        var row = Assert.Single(DadAccountSummaryProjection.Build(local, [remote, remote], Now).Accounts);

        Assert.Equal(conflict, row.HasMultipleLiveSessions);
        if (!conflict)
            Assert.Same(local, row.Participant);
    }

    [Fact]
    public void UnidentifiedConnectionsStayOutsideAccountsAndRetainActualRoleData()
    {
        var local = Participant("local", "");
        local.WorkerRole = DadWorkerRole.ClientDad;
        var coordinator = Participant("coordinator", " ");
        coordinator.WorkerRole = DadWorkerRole.ServerDad;
        var identified = Participant("identified", "account-a");

        var projection = DadAccountSummaryProjection.Build(local, [local, coordinator, identified], Now);

        Assert.Single(projection.Accounts);
        Assert.Equal(2, projection.Unidentified.Count);
        Assert.Contains(local, projection.Unidentified);
        Assert.Contains(coordinator, projection.Unidentified);
        Assert.Equal(DadWorkerRole.ClientDad, local.WorkerRole);
        Assert.Equal(DadWorkerRole.ServerDad, coordinator.WorkerRole);
    }

    [Fact]
    public void LargeRepeatedRosterCountsAccountsAndLeavesDetailedCharacterListsIntact()
    {
        var participants = Enumerable.Range(0, 80).Select(index =>
        {
            var participant = Participant($"worker-{index}", $"account-{index}");
            participant.AvailableCharacterKeys = Enumerable.Range(0, 12)
                .Select(character => new DadCharacterKey($"character-{index}-{character}")).ToList();
            return participant;
        }).ToList();
        var observations = participants.SelectMany(participant => Enumerable.Repeat(participant, 5)).ToList();

        var projection = DadAccountSummaryProjection.Build(participants[0], observations, Now);

        Assert.Equal(80, projection.Accounts.Count);
        Assert.Empty(projection.Unidentified);
        Assert.All(projection.Accounts, row => Assert.False(row.HasMultipleLiveSessions));
        Assert.All(participants, participant => Assert.Equal(12, participant.AvailableCharacterKeys.Count));
        Assert.Equal(400, observations.Count);
    }

    private static DadParticipantSnapshot Participant(string session, string account, int ageSeconds = 0)
        => new()
        {
            WorkerSessionId = new(session), ManagedAccountKey = new(account),
            ManagedAccountAlias = "Synthetic crew", ActiveCharacterKey = new("synthetic-character"),
            State = DadParticipantState.Ready, LastHeartbeatUtc = Now.AddSeconds(-ageSeconds),
        };
}
