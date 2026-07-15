using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRequestedJobContractTests
{
    [Fact]
    public void WakeRequestRoundTripsExactContentAndRequestedJob()
    {
        var request = new DadWakeRequestDto
        {
            RunId = "run-1",
            AuthorityWorkerSessionId = new DadWorkerSessionId("worker-w"),
            RequiredAccountKey = new DadAccountKey("account-x"),
            RequiredCharacterKey = new DadCharacterKey("Venat Azem@Excalibur"),
            RequiredContentId = 123456789,
            RequiredJobId = 21,
            AssignedSlotId = "Slot2",
            CoordinatorTravelTarget = new DadCoordinatorTravelTarget
            {
                RunId = "run-1",
                CoordinatorWorkerSessionId = "worker-w",
                CoordinatorAccountKey = "account-lead",
                CoordinatorCharacterKey = "Leader@Siren",
                CoordinatorContentId = 987654321,
                WorldId = 23,
                WorldName = "Siren",
                DataCenterId = 4,
                DataCenterName = "Aether",
                RegionId = 2,
                RegionName = "North America",
                CapturedAtUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            },
        };

        var roundTrip = DadIpcJson.Deserialize<DadWakeRequestDto>(DadIpcJson.Serialize(request));

        Assert.NotNull(roundTrip);
        Assert.Equal(request.RequiredContentId, roundTrip.RequiredContentId);
        Assert.Equal(request.RequiredJobId, roundTrip.RequiredJobId);
        Assert.Equal(request.RequiredCharacterKey.Value, roundTrip.RequiredCharacterKey.Value);
        Assert.Equal("Siren", roundTrip.CoordinatorTravelTarget!.WorldName);
        Assert.Equal((uint)4, roundTrip.CoordinatorTravelTarget.DataCenterId);
    }

    [Fact]
    public void ParticipantCloneAndJsonCannotAliasRequestedJobProof()
    {
        var participant = Participant(DadRequestedJobPreparationStatus.Switched);

        var clone = participant.Clone();
        clone.RequestedJobPreparation!.Status = DadRequestedJobPreparationStatus.Cancelled;
        clone.CurrentLocation!.WorldName = "Changed";
        var roundTrip = DadIpcJson.Deserialize<DadParticipantSnapshot>(DadIpcJson.Serialize(participant));

        Assert.Equal(DadRequestedJobPreparationStatus.Switched, participant.RequestedJobPreparation!.Status);
        Assert.Equal("Excalibur", participant.CurrentLocation!.WorldName);
        Assert.NotNull(roundTrip);
        Assert.Equal(DadRequestedJobPreparationStatus.Switched, roundTrip.RequestedJobPreparation!.Status);
        Assert.Equal("Venat Azem@Excalibur", roundTrip.RequestedJobPreparation.Key.CharacterKey.Value);
        Assert.Equal("Excalibur", roundTrip.CurrentLocation!.WorldName);
    }

    [Fact]
    public void TerminalPreparationCreatesOneRuntimeReadinessEdge()
    {
        var participant = Participant(DadRequestedJobPreparationStatus.Pending);
        var tracker = new DadRuntimeReadinessTracker();
        Assert.False(tracker.Observe(DadRuntimeReadinessSignature.Create(participant), out _));

        participant.RequestedJobPreparation!.Status = DadRequestedJobPreparationStatus.SoftFailed;
        var terminal = DadRuntimeReadinessSignature.Create(participant);

        Assert.True(tracker.Observe(terminal, out var revision));
        Assert.Equal(1, revision);
        Assert.False(tracker.Observe(terminal, out var duplicateRevision));
        Assert.Equal(revision, duplicateRevision);
    }

    private static DadParticipantSnapshot Participant(DadRequestedJobPreparationStatus status)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId("worker-x"),
            ManagedAccountKey = new DadAccountKey("account-x"),
            ActiveCharacterKey = new DadCharacterKey("Venat Azem@Excalibur"),
            AssignedSlotId = "Slot2",
            Character = new DadAcquiredCharacter
            {
                CharacterKey = "Venat Azem@Excalibur",
                ContentId = 123456789,
                CurrentJobId = 21,
            },
            CurrentLocation = new DadWorldLocationObservation
            {
                WorldId = 21,
                WorldName = "Excalibur",
                DataCenterId = 6,
                DataCenterName = "Primal",
                RegionId = 2,
                RegionName = "North America",
                ObservedAtUtc = DateTime.UtcNow,
            },
            RequestedJobPreparation = new DadRequestedJobPreparationProof
            {
                Key = new DadRequestedJobPreparationKey(
                    "run-1",
                    new DadWorkerSessionId("worker-x"),
                    "Slot2",
                    new DadAccountKey("account-x"),
                    new DadCharacterKey("Venat Azem@Excalibur"),
                    123456789,
                    21),
                Status = status,
                UpdatedAtUtc = DateTime.UtcNow,
            },
        };
}
