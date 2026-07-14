using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadPartyAssemblyServiceTests
{
    [Fact]
    public void BuildInstructionsKeepsConfiguredLeaderFirst()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(queueAuthority: DadQueueAuthority.Leader);
        var participants = new[]
        {
            Participant("Member@Alpha", 200, isLocal: false, isAuthority: false, slot: "Slot2"),
            Participant("Leader@Alpha", 100, isLocal: true, isAuthority: true, slot: "Slot1"),
        };

        var instructions = service.BuildInstructions(plan, participants, out var blocker);

        Assert.Equal(string.Empty, blocker);
        Assert.Equal(2, instructions.Count);
        Assert.Equal("Leader@Alpha", instructions[0].RequiredCharacterKey.Value);
        Assert.Equal("Slot1", instructions[0].SlotId);
        Assert.Equal(DadAssemblyInstructionKind.FormParty, instructions[0].InstructionKind);
        Assert.Equal("Member@Alpha", instructions[1].RequiredCharacterKey.Value);
        Assert.Equal("Slot2", instructions[1].SlotId);
        Assert.Equal(DadAssemblyInstructionKind.JoinParty, instructions[1].InstructionKind);
    }

    [Fact]
    public void BuildInstructionsBlocksInvalidQueueAuthority()
    {
        var service = new DadPartyAssemblyService();
        var instructions = service.BuildInstructions(
            Plan(queueAuthority: DadQueueAuthority.LanParty),
            [
                Participant("Leader@Alpha", 100, isLocal: true, isAuthority: true, slot: "Slot1"),
                Participant("Member@Alpha", 200, isLocal: false, isAuthority: false, slot: "Slot2"),
            ],
            out var blocker);

        Assert.Empty(instructions);
        Assert.Contains("leader queue authority", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInstructionsBlocksWhenConfiguredLeaderIsNotLeaderSlot()
    {
        var service = new DadPartyAssemblyService();
        var instructions = service.BuildInstructions(
            Plan(queueAuthority: DadQueueAuthority.Leader),
            [
                Participant("Member@Alpha", 200, isLocal: true, isAuthority: true, slot: "Slot2"),
                Participant("Other@Alpha", 300, isLocal: false, isAuthority: false, slot: "Slot1"),
            ],
            out var blocker);

        Assert.Empty(instructions);
        Assert.Contains("Configured leader", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInstructionsBlocksWhenParticipantNotPostArReady()
    {
        var service = new DadPartyAssemblyService();
        var member = Participant("Member@Alpha", 200, isLocal: false, isAuthority: false, slot: "Slot2");
        member.PostArReady = false;

        var instructions = service.BuildInstructions(
            Plan(queueAuthority: DadQueueAuthority.Leader),
            [
                Participant("Leader@Alpha", 100, isLocal: true, isAuthority: true, slot: "Slot1"),
                member,
            ],
            out var blocker);

        Assert.Empty(instructions);
        Assert.Contains("post-AR ready", blocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyPartyMembershipReportsMissingMembers()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(queueAuthority: DadQueueAuthority.Leader);
        var participants = new[]
        {
            Participant("Leader@Alpha", 100, isLocal: true, isAuthority: true, slot: "Slot1"),
            Participant("Member@Alpha", 200, isLocal: false, isAuthority: false, slot: "Slot2"),
        };

        var complete = service.VerifyPartyMembership(
            plan,
            participants,
            [PartyMember("Leader@Alpha", 100), PartyMember("Member@Alpha", 200)],
            out var completeBlocker);
        var missing = service.VerifyPartyMembership(
            plan,
            participants,
            [PartyMember("Leader@Alpha", 100)],
            out var missingBlocker);

        Assert.True(complete);
        Assert.Equal(string.Empty, completeBlocker);
        Assert.False(DadPartyAssemblyService.ShouldDispatchJoinInstruction(
            participants[1],
            [PartyMember("Leader@Alpha", 100), PartyMember("Member@Alpha", 200)]));
        Assert.False(missing);
        Assert.Contains("1/2", missingBlocker, StringComparison.OrdinalIgnoreCase);
        Assert.True(DadPartyAssemblyService.ShouldDispatchJoinInstruction(
            participants[1],
            [PartyMember("Leader@Alpha", 100)]));
    }

    [Fact]
    public void FrozenContentIdDoesNotFallBackToMatchingName()
    {
        var participant = Participant("Hard'carry Gray'parse@Excalibur", 200, isLocal: false, isAuthority: false, slot: "Slot2");
        var wrongIdentity = PartyMember("Hard'carry Gray'parse@Excalibur", 999);

        Assert.False(DadPartyAssemblyService.IsParticipantInParty(participant, [wrongIdentity]));
        Assert.True(DadPartyAssemblyService.ShouldDispatchJoinInstruction(participant, [wrongIdentity]));
    }

    [Fact]
    public void UnexpectedPartyListContentIdRejectsFrozenMembership()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(queueAuthority: DadQueueAuthority.Leader);
        var participants = new[]
        {
            Participant("Leader@Alpha", 100, isLocal: true, isAuthority: true, slot: "Slot1"),
            Participant("Member@Alpha", 200, isLocal: false, isAuthority: false, slot: "Slot2"),
        };

        var decision = service.EvaluatePartyMembership(
            plan,
            participants,
            [PartyMember("Leader@Alpha", 100), PartyMember("Unexpected@Alpha", 999)]);

        Assert.Equal(DadPartyMembershipDisposition.Reject, decision.Disposition);
        Assert.Contains("unexpected", decision.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("999", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossWorldFourOfFourUsesCrossRealmMembersAndCompletesAssembly()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(DadQueueAuthority.Leader, partySize: 4);
        var participants = FourParticipants();
        var partyListReads = 0;
        var crossRealmReads = 0;

        var selectedMembers = DadPartySnapshotSourceRules.Read(
            crossRealmPartyActive: true,
            () =>
            {
                partyListReads++;
                return [PartyMember("Leader@Alpha", 100)];
            },
            () =>
            {
                crossRealmReads++;
                return participants
                    .Select(participant => PartyMember(
                        participant.ActiveCharacterKey.Value,
                        participant.Character.ContentId))
                    .ToList();
            });

        var decision = service.EvaluatePartyMembership(plan, participants, selectedMembers);

        Assert.Equal(0, partyListReads);
        Assert.Equal(1, crossRealmReads);
        Assert.Equal(DadPartyMembershipDisposition.Ready, decision.Disposition);
        Assert.Contains("4/4", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossWorldIncompletePartyWaitsForMissingContentId()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(DadQueueAuthority.Leader, partySize: 4);
        var participants = FourParticipants();

        var selectedMembers = DadPartySnapshotSourceRules.Read(
            crossRealmPartyActive: true,
            () => participants.Select(participant => PartyMember(
                participant.ActiveCharacterKey.Value,
                participant.Character.ContentId)).ToList(),
            () => participants.Take(3).Select(participant => PartyMember(
                participant.ActiveCharacterKey.Value,
                participant.Character.ContentId)).ToList());

        var decision = service.EvaluatePartyMembership(plan, participants, selectedMembers);

        Assert.Equal(DadPartyMembershipDisposition.Wait, decision.Disposition);
        Assert.Contains("3/4", decision.Summary, StringComparison.Ordinal);
        Assert.Contains("400", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SameWorldStillUsesPartyListMembers()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(DadQueueAuthority.Leader, partySize: 4);
        var participants = FourParticipants();
        var partyListReads = 0;
        var crossRealmReads = 0;

        var selectedMembers = DadPartySnapshotSourceRules.Read(
            crossRealmPartyActive: false,
            () =>
            {
                partyListReads++;
                return participants.Select(participant => PartyMember(
                    participant.ActiveCharacterKey.Value,
                    participant.Character.ContentId)).ToList();
            },
            () =>
            {
                crossRealmReads++;
                return [PartyMember("Leader@Alpha", 100)];
            });

        var decision = service.EvaluatePartyMembership(plan, participants, selectedMembers);

        Assert.Equal(1, partyListReads);
        Assert.Equal(0, crossRealmReads);
        Assert.Equal(DadPartyMembershipDisposition.Ready, decision.Disposition);
        Assert.Contains("4/4", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalBestEffortJobReportsProduceAllMissingFourPlayerInstructions()
    {
        var service = new DadPartyAssemblyService();
        var plan = Plan(DadQueueAuthority.Leader, partySize: 4);
        var jobs = new uint[] { 40, 32, 24, 38 };
        var statuses = new[]
        {
            DadRequestedJobPreparationStatus.AlreadyMatched,
            DadRequestedJobPreparationStatus.Switched,
            DadRequestedJobPreparationStatus.AlreadyMatched,
            DadRequestedJobPreparationStatus.SoftFailed,
        };
        var participants = Enumerable.Range(1, 4).Select(index =>
        {
            var participant = Participant(
                index == 1 ? "Leader@Alpha" : index == 4 ? "Hildabrand@Alpha" : $"Member {index}@Alpha",
                (ulong)(100 * index),
                isLocal: index == 1,
                isAuthority: index == 1,
                slot: $"Slot{index}");
            participant.ManagedAccountKey = new DadAccountKey($"account-{index}");
            participant.Character.AccountId = participant.ManagedAccountKey.Value;
            participant.Character.CurrentJobId = statuses[index - 1] == DadRequestedJobPreparationStatus.SoftFailed
                ? 19u
                : jobs[index - 1];
            participant.RequestedJobPreparation = new DadRequestedJobPreparationProof
            {
                Key = new DadRequestedJobPreparationKey(
                    "run",
                    participant.WorkerSessionId,
                    participant.AssignedSlotId,
                    participant.ManagedAccountKey,
                    participant.ActiveCharacterKey,
                    participant.Character.ContentId,
                    jobs[index - 1]),
                Status = statuses[index - 1],
            };
            Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(
                participant.RequestedJobPreparation,
                participant.RequestedJobPreparation.Key,
                participant.Character.CurrentJobId.Value));
            return participant;
        }).ToList();

        var instructions = service.BuildInstructions(plan, participants, out var blocker);

        Assert.Empty(blocker);
        Assert.Equal(4, instructions.Count);
        Assert.Equal(DadAssemblyInstructionKind.FormParty, instructions[0].InstructionKind);
        Assert.Equal(3, instructions.Count(static instruction =>
            instruction.InstructionKind == DadAssemblyInstructionKind.JoinParty));
        var leaderOnly = new[] { PartyMember("Leader@Alpha", 100) };
        Assert.All(participants.Skip(1), participant => Assert.True(
            DadPartyAssemblyService.ShouldDispatchJoinInstruction(participant, leaderOnly)));
        Assert.False(service.VerifyPartyMembership(plan, participants, leaderOnly, out var waiting));
        Assert.Contains("1/4", waiting, StringComparison.OrdinalIgnoreCase);

        var exactParty = participants
            .Select(participant => PartyMember(
                participant.ActiveCharacterKey.Value,
                participant.Character.ContentId))
            .ToList();
        Assert.True(service.VerifyPartyMembership(plan, participants, exactParty, out var complete));
        Assert.Empty(complete);
    }

    private static DadRunPlan Plan(DadQueueAuthority queueAuthority, int partySize = 2)
        => new()
        {
            Request = new DadRunRequest { RequestId = "run" },
            CompositeModuleId = DadModuleId.PremadeDuty,
            RequiredParticipantCount = partySize,
            LeaderCharacterKey = "Leader@Alpha",
            Orchestration = new DadOrchestrationIntent
            {
                QueueAuthority = queueAuthority,
                RosterIntent = new DadRosterIntent { ExpectedPartySize = partySize, RequireRemoteParticipants = true },
            },
        };

    private static DadParticipantSnapshot Participant(string characterKey, ulong contentId, bool isLocal, bool isAuthority, string slot)
        => new()
        {
            ActiveCharacterKey = new DadCharacterKey(characterKey),
            Character = new DadAcquiredCharacter
            {
                CharacterKey = characterKey,
                ContentId = contentId,
                CharacterName = characterKey.Split('@')[0],
                WorldName = "Alpha",
            },
            IsLocalClient = isLocal,
            IsAuthority = isAuthority,
            PostArReady = true,
            AssignedSlotId = slot,
            WorkerSessionId = new DadWorkerSessionId(characterKey),
        };

    private static List<DadParticipantSnapshot> FourParticipants()
        => Enumerable.Range(1, 4)
            .Select(index => Participant(
                index == 1 ? "Leader@Alpha" : $"Member {index}@Alpha",
                (ulong)(index * 100),
                isLocal: index == 1,
                isAuthority: index == 1,
                slot: $"Slot{index}"))
            .ToList();

    private static DadPartyMemberSnapshot PartyMember(string characterKey, ulong contentId)
        => new()
        {
            CharacterKey = new DadCharacterKey(characterKey),
            ContentId = contentId,
            CharacterName = characterKey.Split('@')[0],
            WorldName = "Alpha",
        };
}
