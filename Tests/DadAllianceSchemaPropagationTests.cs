using dad.Models;
using dad.Services;
using Newtonsoft.Json;
using Xunit;

namespace dad.Tests;

public sealed class DadAllianceSchemaPropagationTests
{
    [Fact]
    public void VersionSixMigrationDefaultsLegacySlotsToNoneAndRequiresOperatorAssignment()
    {
        const string legacyJson =
            """
            {
              "Version": 6,
              "PlannerGroups": [
                {
                  "GroupId": "legacy-fixture",
                  "Slots": [
                    { "SlotId": "Slot1" },
                    { "SlotId": "Slot2" },
                    { "SlotId": "Slot3" }
                  ]
                }
              ]
            }
            """;
        var configuration = JsonConvert.DeserializeObject<Configuration>(legacyJson)!;

        var changed = configuration.MigrateTransportSettings();

        Assert.True(changed);
        Assert.Equal(7, configuration.Version);
        var slots = Assert.Single(configuration.PlannerGroups).Slots;
        Assert.All(slots, static slot => Assert.Equal(DadAllianceAssignment.None, slot.AllianceAssignment));
        Assert.False(DadAlliancePartyFinderRules.ValidateSavedRows(slots).IsValid);
    }

    [Fact]
    public void HubProtocolFourRejectsMixedVersionThreeFrames()
    {
        Assert.Equal(4, DadHubProtocol.CurrentVersion);
        var frame = DadHubProtocol.CreateFrame(
            DadHubFrameKind.Hello,
            new DadWorkerSessionId("worker-fixture"),
            new DadWorkerSessionId(string.Empty),
            "hello",
            "correlation-fixture",
            "{}",
            string.Empty);
        frame.ProtocolVersion = 3;

        var error = Assert.Throws<DadHubProtocolException>(
            () => DadHubProtocol.ValidateFrame(frame, string.Empty));

        Assert.Equal("protocol-mismatch", error.Code);
        Assert.Contains("4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigAndIpcRoundTripsPreserveAllianceAssignment()
    {
        var group = GroupWithAssignments();

        var configRoundTrip = JsonConvert.DeserializeObject<DadPlannerGroup>(
            JsonConvert.SerializeObject(group));
        var ipcRoundTrip = DadIpcJson.Deserialize<DadPlannerGroup>(DadIpcJson.Serialize(group));

        Assert.NotNull(configRoundTrip);
        Assert.NotNull(ipcRoundTrip);
        AssertAssignments(configRoundTrip!);
        AssertAssignments(ipcRoundTrip!);
    }

    [Fact]
    public void HubIpcRoundTripPreservesExactAllianceRecruitmentContract()
    {
        var instruction = new DadAllianceRecruitmentInstructionDto
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            CoordinatorWorkerSessionId = new DadWorkerSessionId("coordinator-worker"),
            CoordinatorIdentity = "coordinator-fixture",
            LeaderName = "Host Example",
            LeaderWorld = "Alpha",
            TargetWorkerSessionId = new DadWorkerSessionId("target-worker"),
            TargetApplicationId = 300,
            TargetCharacterKey = new DadCharacterKey("Target Example@Beta"),
            TargetCharacterName = "Target Example",
            TargetCharacterWorld = "Beta",
            TargetContentId = 400,
            AssignedAlliance = DadAllianceAssignment.G,
            Passcode = 6789,
            Attempt = 12,
            State = DadAllianceRecruitmentState.RetryWaiting,
            StopGeneration = 5,
        };

        var restored = DadIpcJson.Deserialize<DadAllianceRecruitmentInstructionDto>(
            DadIpcJson.Serialize(instruction));

        Assert.NotNull(restored);
        Assert.Equal(instruction.RecruitmentId, restored.RecruitmentId);
        Assert.Equal(instruction.CoordinatorWorkerSessionId, restored.CoordinatorWorkerSessionId);
        Assert.Equal(instruction.CoordinatorIdentity, restored.CoordinatorIdentity);
        Assert.Equal(instruction.LeaderName, restored.LeaderName);
        Assert.Equal(instruction.LeaderWorld, restored.LeaderWorld);
        Assert.Equal(instruction.TargetWorkerSessionId, restored.TargetWorkerSessionId);
        Assert.Equal(instruction.TargetApplicationId, restored.TargetApplicationId);
        Assert.Equal(instruction.TargetCharacterKey, restored.TargetCharacterKey);
        Assert.Equal(instruction.TargetContentId, restored.TargetContentId);
        Assert.Equal(DadAllianceAssignment.G, restored.AssignedAlliance);
        Assert.Equal(6789, restored.Passcode);
        Assert.Equal(12, restored.Attempt);
        Assert.Equal(DadAllianceRecruitmentState.RetryWaiting, restored.State);
        Assert.Equal(5, restored.StopGeneration);
        Assert.Empty(DadAlliancePartyFinderRules.ValidateInstruction(restored));
    }

    [Fact]
    public void SchedulerCloneStateAndPlannerRefreshPreserveAssignments()
    {
        var group = GroupWithAssignments();
        var clone = DadSchedulerGroupCloneRules.CloneWithSlots(group, group.Slots);
        var state = new DadSchedulerSlotState
        {
            SlotId = "Slot1",
            AllianceAssignment = DadAllianceAssignment.G,
        };
        var clonedState = state.Clone();
        var refreshed = DadPlannerGroupUpdateRules.RefreshSlotsPreservingOperationalSettings(
            group.Slots,
            [
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot1",
                    AllianceAssignment = DadAllianceAssignment.G,
                },
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot2",
                    AllianceAssignment = DadAllianceAssignment.G,
                },
                new DadPlannerGroupSlot
                {
                    SlotId = "Slot3",
                    AllianceAssignment = DadAllianceAssignment.G,
                },
            ]);

        AssertAssignments(clone);
        Assert.Equal(DadAllianceAssignment.G, clonedState.AllianceAssignment);
        Assert.All(
            refreshed,
            static slot => Assert.Equal(DadAllianceAssignment.G, slot.AllianceAssignment));
    }

    [Fact]
    public void EffectiveSubstitutionUsesThePrimaryConfiguredAlliance()
    {
        var group = GroupWithAssignments();
        group.Slots[0].AllianceAssignment = DadAllianceAssignment.G;
        group.Slots.Insert(1, new DadPlannerGroupSlot
        {
            SlotId = "Slot1",
            IsSubstitute = true,
            AllianceAssignment = DadAllianceAssignment.C,
            RequiredCharacterKey = new DadCharacterKey("Substitute Example@Beta"),
        });
        var projected = DadEffectivePlannerGroupProjection.Project(
            group,
            DadPlannerActivityMode.DutyPremade,
            requestedPartySize: 3);
        var bound = DadEffectivePlannerGroupProjection.BindResolvedSchedulerSlots(
            projected,
            [
                new DadPresetCharacterSlot
                {
                    SlotId = "Slot1",
                    AllianceAssignment = DadAllianceAssignment.G,
                    CharacterKey = "Substitute Example@Beta",
                    RequiredCharacterKey = new DadCharacterKey("Substitute Example@Beta"),
                    ContentId = 1234,
                    IsSubstitution = true,
                },
            ]);

        Assert.Equal(DadAllianceAssignment.G, projected.Slots[0].AllianceAssignment);
        Assert.Equal(DadAllianceAssignment.G, projected.Slots[1].AllianceAssignment);
        Assert.Equal(DadAllianceAssignment.G, bound.Slots[0].AllianceAssignment);
    }

    [Fact]
    public void TemplateCreationAndInstantiationPreserveAssignments()
    {
        var group = GroupWithAssignments();

        var template = DadPresetTemplateService.CreateTemplateFrom(
            group,
            "Alliance template",
            DateTime.UtcNow);
        var instance = DadPresetTemplateService.Instantiate(
            template,
            new DadCharacterPool(),
            DateTime.UtcNow);

        AssertAssignments(template);
        AssertAssignments(instance);
    }

    private static DadPlannerGroup GroupWithAssignments()
        => new()
        {
            GroupId = "alliance-fixture",
            DisplayName = "Synthetic Alliance Fixture",
            Slots =
            [
                Slot("Slot1", DadAllianceAssignment.A),
                Slot("Slot2", DadAllianceAssignment.B),
                Slot("Slot3", DadAllianceAssignment.G),
            ],
        };

    private static DadPlannerGroupSlot Slot(string slotId, DadAllianceAssignment assignment)
        => new()
        {
            SlotId = slotId,
            AllianceAssignment = assignment,
            RequiredCharacterKey = new DadCharacterKey($"{slotId} Example@Alpha"),
        };

    private static void AssertAssignments(DadPlannerGroup group)
        => Assert.Equal(
            [DadAllianceAssignment.A, DadAllianceAssignment.B, DadAllianceAssignment.G],
            group.Slots.Select(static slot => slot.AllianceAssignment).ToArray());
}
