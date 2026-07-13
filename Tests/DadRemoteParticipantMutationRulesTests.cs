using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadRemoteParticipantMutationRulesTests
{
    [Fact]
    public void SelfLocalRemoteResponseUpdatesReadinessButNotCoordinatorOwnership()
    {
        var target = Participant();
        var source = Participant();
        source.IsLocalClient = true;
        source.IsAuthority = true;
        source.Role = DadOrchestrationRole.Leader;
        source.WorkerRole = DadWorkerRole.ServerDad;
        source.State = DadParticipantState.Claimed;
        source.ClaimState = DadClaimState.Granted;
        source.LeaseState = DadParticipantLeaseState.Granted;
        source.PostArReady = true;
        source.StatusText = "X is ready.";

        var applied = DadRemoteParticipantMutationRules.TryApplyIdentityValidRuntimeState(
            target,
            source,
            Slot(),
            "run",
            out var blocker);

        Assert.True(applied);
        Assert.Equal(string.Empty, blocker);
        Assert.False(target.IsLocalClient);
        Assert.False(target.IsAuthority);
        Assert.Equal(DadOrchestrationRole.Participant, target.Role);
        Assert.Equal(DadWorkerRole.ClientDad, target.WorkerRole);
        Assert.Equal("worker-x", target.WorkerSessionId.Value);
        Assert.Equal("Slot2", target.AssignedSlotId);
        Assert.Equal("account-x", target.ManagedAccountKey.Value);
        Assert.Equal("Hard'carry Gray'parse@Excalibur", target.ActiveCharacterKey.Value);
        Assert.Equal(200ul, target.Character.ContentId);
        Assert.Equal(DadParticipantState.Claimed, target.State);
        Assert.Equal(DadClaimState.Granted, target.ClaimState);
        Assert.Equal(DadParticipantLeaseState.Granted, target.LeaseState);
        Assert.True(target.PostArReady);
        Assert.Equal("X is ready.", target.StatusText);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("worker")]
    [InlineData("slot")]
    [InlineData("account")]
    [InlineData("character")]
    [InlineData("content")]
    public void WrongRemoteIdentityCannotMutateFrozenProjection(string mismatch)
    {
        var target = Participant();
        var source = Participant();
        source.State = DadParticipantState.Claimed;
        source.ClaimState = DadClaimState.Granted;
        source.PostArReady = true;
        switch (mismatch)
        {
            case "run":
                source.RunId = "wrong-run";
                break;
            case "worker":
                source.WorkerSessionId = new DadWorkerSessionId("wrong-worker");
                break;
            case "slot":
                source.AssignedSlotId = "Slot1";
                break;
            case "account":
                source.ManagedAccountKey = new DadAccountKey("wrong-account");
                break;
            case "character":
                source.ActiveCharacterKey = new DadCharacterKey("Wrong Character@Excalibur");
                break;
            case "content":
                source.Character.ContentId = 999;
                break;
        }

        var applied = DadRemoteParticipantMutationRules.TryApplyIdentityValidRuntimeState(
            target,
            source,
            Slot(),
            "run",
            out var blocker);

        Assert.False(applied);
        Assert.NotEqual(string.Empty, blocker);
        Assert.Equal(DadParticipantState.Discovered, target.State);
        Assert.Equal(DadClaimState.None, target.ClaimState);
        Assert.False(target.PostArReady);
        Assert.False(target.IsLocalClient);
        Assert.Equal("worker-x", target.WorkerSessionId.Value);
        Assert.Equal("Slot2", target.AssignedSlotId);
        Assert.Equal("account-x", target.ManagedAccountKey.Value);
        Assert.Equal("Hard'carry Gray'parse@Excalibur", target.ActiveCharacterKey.Value);
        Assert.Equal(200ul, target.Character.ContentId);
    }

    private static DadFrozenRunSlot Slot()
        => new()
        {
            SlotId = "Slot2",
            AccountKey = new DadAccountKey("account-x"),
            CharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            ContentId = 200,
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
        };

    private static DadParticipantSnapshot Participant()
        => new()
        {
            RunId = "run",
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            Role = DadOrchestrationRole.Participant,
            WorkerRole = DadWorkerRole.ClientDad,
            State = DadParticipantState.Discovered,
            ClaimState = DadClaimState.None,
            LeaseState = DadParticipantLeaseState.None,
            IsLocalClient = false,
            IsAuthority = false,
            IsAvailable = true,
            IsEligibleForRun = true,
            PostArReady = false,
            ManagedAccountKey = new DadAccountKey("account-x"),
            ActiveCharacterKey = new DadCharacterKey("Hard'carry Gray'parse@Excalibur"),
            Character = new DadAcquiredCharacter
            {
                CharacterKey = "Hard'carry Gray'parse@Excalibur",
                ContentId = 200,
                CharacterName = "Hard'carry Gray'parse",
                WorldId = 21,
            },
            AssignedSlotId = "Slot2",
        };
}
