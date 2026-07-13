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

    private static DadRunPlan Plan(DadQueueAuthority queueAuthority)
        => new()
        {
            Request = new DadRunRequest { RequestId = "run" },
            CompositeModuleId = DadModuleId.PremadeDuty,
            RequiredParticipantCount = 2,
            LeaderCharacterKey = "Leader@Alpha",
            Orchestration = new DadOrchestrationIntent
            {
                QueueAuthority = queueAuthority,
                RosterIntent = new DadRosterIntent { ExpectedPartySize = 2, RequireRemoteParticipants = true },
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

    private static DadPartyMemberSnapshot PartyMember(string characterKey, ulong contentId)
        => new()
        {
            CharacterKey = new DadCharacterKey(characterKey),
            ContentId = contentId,
            CharacterName = characterKey.Split('@')[0],
            WorldName = "Alpha",
        };
}
