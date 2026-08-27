using dad.Models;
using dad.Services;
using Newtonsoft.Json;
using System.Collections.Immutable;
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

        var changed = DadAutoPartyConfigurationMigration.Migrate(
            configuration,
            new MissingAutoPartyIdentityStore(),
            new MissingAutoPartyWebhookStore());

        Assert.True(changed);
        Assert.Equal(12, configuration.Version);
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
            TargetIslandId = "island-target",
            TargetOwnerId = "owner-target",
            TargetOpaqueCharacterId = "opaque-target",
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
        Assert.Equal(instruction.TargetIslandId, restored.TargetIslandId);
        Assert.Equal(instruction.TargetOwnerId, restored.TargetOwnerId);
        Assert.Equal(instruction.TargetOpaqueCharacterId, restored.TargetOpaqueCharacterId);
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
    public void AllianceRoutingUsesRegisteredIslandsWithoutDiscordEnvelopeModels()
    {
        var instructionProperties = typeof(DadAllianceRecruitmentInstructionDto)
            .GetProperties()
            .Select(static property => property.Name)
            .ToList();
        var targetProperties = typeof(DadAllianceRecruitmentTarget)
            .GetProperties()
            .Select(static property => property.Name)
            .ToList();
        var assembly = typeof(DadAllianceRecruitmentInstructionDto).Assembly;

        Assert.Contains(nameof(DadAllianceRecruitmentInstructionDto.TargetIslandId), instructionProperties);
        Assert.DoesNotContain("TargetApplicationId", instructionProperties);
        Assert.Contains(nameof(DadAllianceRecruitmentTarget.RegisteredIslandId), targetProperties);
        Assert.Contains(nameof(DadAllianceRecruitmentTarget.OwnerId), targetProperties);
        Assert.Contains(nameof(DadAllianceRecruitmentTarget.OpaqueCharacterId), targetProperties);
        Assert.DoesNotContain("DiscordApplicationId", targetProperties);
        Assert.Null(assembly.GetType("dad.Models.DadAllianceDiscordEnvelope", throwOnError: false));
        Assert.Null(assembly.GetType("dad.Models.DadAllianceDiscordValidationContext", throwOnError: false));
    }

    [Fact]
    public void RegisteredIslandAllianceContractsMapOnlyTypedCentralIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var header = ContractHeader("requester-island", "target-island", now);
        var recruitmentId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var instruction = new DadAllianceRecruitmentInstructionDto
        {
            RecruitmentId = recruitmentId.ToString("N"),
            CoordinatorWorkerSessionId = new DadWorkerSessionId("private-coordinator-worker"),
            CoordinatorIdentity = "private-coordinator-identity",
            LeaderName = "Leader Example",
            LeaderWorld = "Alpha",
            TargetWorkerSessionId = new DadWorkerSessionId("private-target-worker"),
            TargetIslandId = "target-island",
            TargetOwnerId = "target-owner",
            TargetOpaqueCharacterId = "opaque-target",
            TargetCharacterKey = new DadCharacterKey("Private Target@Beta"),
            TargetCharacterName = "Private Target",
            TargetCharacterWorld = "Beta",
            TargetContentId = 1234,
            AssignedAlliance = DadAllianceAssignment.A,
            CreateListingAsHost = true,
            Passcode = 6789,
            Attempt = 3,
            State = DadAllianceRecruitmentState.CreatingListing,
            StopGeneration = 4,
            IssuedAtUtc = now.UtcDateTime.AddMinutes(-5),
        };

        var operation = DadAllianceAutoPartyContractMapping.ToRecruitOperation(
            instruction,
            header,
            operationId);
        Assert.NotEmpty(AutoParty.Contracts.CanonicalCborCodec.EncodeUnsigned(operation));
        var restoredInstruction = DadAllianceAutoPartyContractMapping.FromRecruitOperation(operation);

        Assert.Equal(operationId, operation.OperationId);
        Assert.Equal(recruitmentId, operation.RecruitmentId);
        Assert.Equal("target-owner", operation.TargetOwnerId.Value);
        Assert.Equal("opaque-target", operation.TargetCharacterId.Value);
        Assert.Equal("target-island", restoredInstruction.TargetIslandId);
        Assert.Equal(recruitmentId.ToString("N"), restoredInstruction.RecruitmentId);
        Assert.Equal("target-owner", restoredInstruction.TargetOwnerId);
        Assert.Equal("opaque-target", restoredInstruction.TargetOpaqueCharacterId);
        Assert.True(restoredInstruction.TargetWorkerSessionId.IsEmpty);
        Assert.True(restoredInstruction.TargetCharacterKey.IsEmpty);
        Assert.Equal(string.Empty, restoredInstruction.TargetCharacterName);
        Assert.Equal(0UL, restoredInstruction.TargetContentId);
        Assert.Equal(string.Empty, restoredInstruction.CoordinatorIdentity);
        Assert.Equal(now.UtcDateTime, restoredInstruction.IssuedAtUtc);
        Assert.True(restoredInstruction.CreateListingAsHost);
        Assert.Equal(DadAllianceAssignment.A, restoredInstruction.AssignedAlliance);
        Assert.Equal(DadAllianceRecruitmentState.CreatingListing, restoredInstruction.State);

        var nonHostInstruction = instruction.Clone();
        nonHostInstruction.CreateListingAsHost = false;
        nonHostInstruction.AssignedAlliance = DadAllianceAssignment.G;
        nonHostInstruction.State = DadAllianceRecruitmentState.Searching;
        var nonHostOperation = DadAllianceAutoPartyContractMapping.ToRecruitOperation(
            nonHostInstruction,
            header,
            Guid.NewGuid());
        Assert.NotEmpty(AutoParty.Contracts.CanonicalCborCodec.EncodeUnsigned(nonHostOperation));
        var restoredNonHost = DadAllianceAutoPartyContractMapping.FromRecruitOperation(nonHostOperation);
        Assert.False(restoredNonHost.CreateListingAsHost);
        Assert.Equal(DadAllianceAssignment.G, restoredNonHost.AssignedAlliance);
        Assert.Equal(DadAllianceRecruitmentState.Searching, restoredNonHost.State);

        var contradictoryHost = instruction.Clone();
        contradictoryHost.AssignedAlliance = DadAllianceAssignment.G;
        Assert.Throws<ArgumentException>(() => DadAllianceAutoPartyContractMapping.ToRecruitOperation(
            contradictoryHost,
            header,
            Guid.NewGuid()));
        var contradictoryNonHost = nonHostInstruction.Clone();
        contradictoryNonHost.State = DadAllianceRecruitmentState.CreatingListing;
        Assert.Throws<ArgumentException>(() => DadAllianceAutoPartyContractMapping.ToRecruitOperation(
            contradictoryNonHost,
            header,
            Guid.NewGuid()));

        var cancellation = new DadAllianceRecruitmentCancellationDto
        {
            RecruitmentId = recruitmentId.ToString("D"),
            TargetIslandId = "target-island",
            TargetOwnerId = "target-owner",
            TargetOpaqueCharacterId = "opaque-target",
            TargetWorkerSessionId = new DadWorkerSessionId("private-target-worker"),
            TargetCharacterKey = new DadCharacterKey("Private Target@Beta"),
            StopGeneration = 5,
            RequestedAtUtc = now.UtcDateTime.AddMinutes(-5),
            Reason = "dad-owner-stop",
        };
        var cancelOperation = DadAllianceAutoPartyContractMapping.ToCancelOperation(
            cancellation,
            header,
            Guid.NewGuid());
        Assert.NotEmpty(AutoParty.Contracts.CanonicalCborCodec.EncodeUnsigned(cancelOperation));
        var restoredCancellation = DadAllianceAutoPartyContractMapping.FromCancelOperation(cancelOperation);
        Assert.Equal(AutoParty.Contracts.AllianceRecruitmentOperationKind.Cancel, cancelOperation.Kind);
        Assert.Equal(recruitmentId.ToString("N"), restoredCancellation.RecruitmentId);
        Assert.Equal("target-owner", restoredCancellation.TargetOwnerId);
        Assert.Equal("opaque-target", restoredCancellation.TargetOpaqueCharacterId);
        Assert.True(restoredCancellation.TargetWorkerSessionId.IsEmpty);
        Assert.True(restoredCancellation.TargetCharacterKey.IsEmpty);
        Assert.Equal(now.UtcDateTime, restoredCancellation.RequestedAtUtc);

        var result = new DadAllianceRecruitmentResultDto
        {
            RecruitmentId = recruitmentId.ToString("D"),
            ParticipantOwnerId = "target-owner",
            TargetOpaqueCharacterId = "opaque-target",
            WorkerSessionId = new DadWorkerSessionId("private-target-worker"),
            TargetCharacterKey = new DadCharacterKey("Private Target@Beta"),
            TargetCharacterName = "Private Target",
            TargetContentId = 1234,
            ExpectedAlliance = DadAllianceAssignment.G,
            ObservedAlliance = DadAllianceAssignment.G,
            Attempt = 3,
            State = DadAllianceRecruitmentState.Complete,
            ResultKind = DadAllianceRecruitmentResultKind.Succeeded,
            StopGeneration = 4,
            Summary = "Private native details must not cross central transport.",
        };
        var receipt = DadAllianceAutoPartyContractMapping.ToReceipt(result, header, operationId);
        Assert.NotEmpty(AutoParty.Contracts.CanonicalCborCodec.EncodeUnsigned(receipt));
        var restoredResult = DadAllianceAutoPartyContractMapping.FromReceipt(receipt);
        Assert.Equal("target-owner", receipt.ParticipantOwnerId.Value);
        Assert.Equal("opaque-target", receipt.TargetCharacterId.Value);
        Assert.Equal("target-owner", restoredResult.ParticipantOwnerId);
        Assert.Equal(recruitmentId.ToString("N"), restoredResult.RecruitmentId);
        Assert.Equal("opaque-target", restoredResult.TargetOpaqueCharacterId);
        Assert.True(restoredResult.WorkerSessionId.IsEmpty);
        Assert.True(restoredResult.TargetCharacterKey.IsEmpty);
        Assert.Equal(string.Empty, restoredResult.TargetCharacterName);
        Assert.Equal(0UL, restoredResult.TargetContentId);
        Assert.Equal("dad-alliance-succeeded", restoredResult.Summary);

        var wireProperties = typeof(AutoParty.Contracts.AllianceRecruitmentOperation)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(nameof(DadAllianceRecruitmentInstructionDto.TargetWorkerSessionId), wireProperties);
        Assert.DoesNotContain(nameof(DadAllianceRecruitmentInstructionDto.TargetCharacterKey), wireProperties);
        Assert.DoesNotContain(nameof(DadAllianceRecruitmentInstructionDto.TargetCharacterName), wireProperties);
        Assert.DoesNotContain(nameof(DadAllianceRecruitmentInstructionDto.TargetContentId), wireProperties);
        Assert.DoesNotContain(nameof(DadAllianceRecruitmentInstructionDto.CoordinatorIdentity), wireProperties);
        Assert.DoesNotContain(nameof(DadAllianceRecruitmentInstructionDto.IssuedAtUtc), wireProperties);
    }

    [Fact]
    public void AdditiveSlotOneAssemblyAndRemoteHostFieldsCloneAndRoundTrip()
    {
        var assembly = new DadAssemblyInstructionDto
        {
            RunId = "run",
            AuthorityWorkerSessionId = new DadWorkerSessionId("coordinator-worker"),
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = "Slot1",
            RequiredCharacterKey = new DadCharacterKey("Leader@Alpha"),
            InstructionKind = DadAssemblyInstructionKind.FormParty,
            FrozenInviter = new DadExpectedPartyInviter
            {
                RunId = "run",
                WorkerSessionId = new DadWorkerSessionId("slot1-worker"),
                AccountKey = new DadAccountKey("slot1-account"),
                CharacterKey = new DadCharacterKey("Leader@Alpha"),
                ContentId = 100,
                CharacterName = "Leader",
                WorldId = 1,
            },
            InviteTargets =
            [
                new DadNativePartyInviteTarget
                {
                    RunId = "run",
                    ModuleId = DadModuleId.PremadeDuty,
                    SlotId = "Slot2",
                    AccountKey = new DadAccountKey("slot2-account"),
                    CharacterKey = new DadCharacterKey("Member@Beta"),
                    ContentId = 200,
                    CharacterName = "Member",
                    WorldId = 2,
                    WorkerSessionId = new DadWorkerSessionId("slot2-worker"),
                },
            ],
        };
        var result = new DadRunStepResultDto
        {
            RunId = "run",
            AuthoritativePartyMembers =
            [
                new DadPartyMemberSnapshot { ContentId = 100, CharacterName = "Leader" },
                new DadPartyMemberSnapshot { ContentId = 200, CharacterName = "Member" },
            ],
        };
        var host = new DadAllianceRecruitmentInstructionDto
        {
            RecruitmentId = Guid.NewGuid().ToString("N"),
            CoordinatorWorkerSessionId = new DadWorkerSessionId("coordinator-worker"),
            CoordinatorIdentity = "coordinator",
            LeaderName = "Leader",
            LeaderWorld = "Alpha",
            TargetWorkerSessionId = new DadWorkerSessionId("slot1-worker"),
            TargetCharacterKey = new DadCharacterKey("Leader@Alpha"),
            TargetCharacterName = "Leader",
            TargetCharacterWorld = "Alpha",
            TargetContentId = 100,
            AssignedAlliance = DadAllianceAssignment.A,
            CreateListingAsHost = true,
            Passcode = 1234,
        };

        var assemblyRoundTrip = DadIpcJson.Deserialize<DadAssemblyInstructionDto>(
            DadIpcJson.Serialize(assembly))!;
        var resultRoundTrip = DadIpcJson.Deserialize<DadRunStepResultDto>(
            DadIpcJson.Serialize(result))!;
        var hostRoundTrip = DadIpcJson.Deserialize<DadAllianceRecruitmentInstructionDto>(
            DadIpcJson.Serialize(host))!;

        Assert.Equal("slot1-worker", assembly.Clone().FrozenInviter.WorkerSessionId.Value);
        Assert.Equal((ulong)200, Assert.Single(assemblyRoundTrip.InviteTargets).ContentId);
        Assert.Equal([100UL, 200UL], result.Clone().AuthoritativePartyMembers.Select(static member => member.ContentId));
        Assert.Equal(2, resultRoundTrip.AuthoritativePartyMembers.Count);
        Assert.True(host.Clone().CreateListingAsHost);
        Assert.True(hostRoundTrip.CreateListingAsHost);
        Assert.Empty(DadAlliancePartyFinderRules.ValidateInstruction(hostRoundTrip));
    }

    [Fact]
    public void AppendedLeavePartyAndResolvedTargetsPreserveWireAndCloneContracts()
    {
        Assert.Equal(6, (int)DadAssemblyInstructionKind.ReadyCheck);
        Assert.Equal(7, (int)DadAssemblyInstructionKind.LeaveParty);

        var instruction = new DadAssemblyInstructionDto
        {
            RunId = "run",
            AuthorityWorkerSessionId = new DadWorkerSessionId("coordinator-worker"),
            ModuleId = DadModuleId.PremadeDuty,
            SlotId = "Slot2",
            RequiredCharacterKey = new DadCharacterKey("Follower@Beta"),
            InstructionKind = DadAssemblyInstructionKind.LeaveParty,
        };
        var instructionRoundTrip = DadIpcJson.Deserialize<DadAssemblyInstructionDto>(
            DadIpcJson.Serialize(instruction))!;
        Assert.Equal(DadAssemblyInstructionKind.LeaveParty, instruction.Clone().InstructionKind);
        Assert.Equal(DadAssemblyInstructionKind.LeaveParty, instructionRoundTrip.InstructionKind);

        var request = new DadRunRequest
        {
            StopPolicy = new DadRunStopPolicy
            {
                Mode = DadPlannerStopMode.TargetLevel,
                ResolvedLevelTargets =
                [
                    new DadResolvedLevelTarget
                    {
                        CharacterKey = new DadCharacterKey("Follower@Beta"),
                        CharacterLabel = "Follower@Beta",
                        JobId = 19,
                        TargetLevel = 90,
                    },
                ],
            },
        };
        var clonedPolicy = request.StopPolicy.Clone();
        clonedPolicy.ResolvedLevelTargets[0].TargetLevel = 99;
        var requestRoundTrip = DadIpcJson.Deserialize<DadRunRequest>(
            DadIpcJson.Serialize(request))!;

        Assert.Equal(90, request.StopPolicy.ResolvedLevelTargets[0].TargetLevel);
        Assert.Equal(90, requestRoundTrip.StopPolicy.ResolvedLevelTargets[0].TargetLevel);
        Assert.Equal((uint?)19, requestRoundTrip.StopPolicy.ResolvedLevelTargets[0].JobId);
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

    private static AutoParty.Contracts.ContractHeader ContractHeader(
        string senderIsland,
        string recipientIsland,
        DateTimeOffset now)
        => new(
            AutoParty.Contracts.AutoPartyProtocol.CurrentVersion,
            Guid.NewGuid(),
            $"alliance-test-{Guid.NewGuid():N}",
            new AutoParty.Contracts.IslandId(senderIsland),
            new AutoParty.Contracts.IslandId(recipientIsland),
            now,
            now.AddMinutes(5),
            1,
            1,
            1,
            1,
            AutoParty.Contracts.ContractHeader.CreateNonce(new byte[AutoParty.Contracts.AutoPartyProtocol.ContractNonceBytes]),
            ImmutableArray<int>.Empty);
}
