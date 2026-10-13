using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadLifecycleHardeningTransportTests
{
    [Fact]
    public void ParticipantIdentityMustExactlyMatchAuthenticatedWorkerSession()
    {
        var participant = new DadParticipantSnapshot
        {
            WorkerSessionId = new DadWorkerSessionId("worker-a"),
        };

        Assert.True(DadHubParticipantIdentityRules.MatchesAuthenticatedSource(
            participant,
            new DadWorkerSessionId("WORKER-A")));
        Assert.False(DadHubParticipantIdentityRules.MatchesAuthenticatedSource(
            participant,
            new DadWorkerSessionId("worker-b")));
        Assert.False(DadHubParticipantIdentityRules.MatchesAuthenticatedSource(
            participant,
            new DadWorkerSessionId(string.Empty)));
        Assert.False(DadHubParticipantIdentityRules.MatchesAuthenticatedSource(
            new DadParticipantSnapshot(),
            new DadWorkerSessionId("worker-a")));
        Assert.False(DadHubParticipantIdentityRules.MatchesAuthenticatedSource(
            null,
            new DadWorkerSessionId("worker-a")));
    }

    [Fact]
    public void ExplicitNullOptionalCollectionsNormalizeToEmpty()
    {
        const string json = """
            {
              "planId": "plan",
              "characterRefs": null,
              "accountKeys": null,
              "characterKeys": null,
              "diagnosticsReason": null
            }
            """;

        var plan = DadIpcJson.Deserialize<DadRosterRefreshPlan>(json);

        Assert.NotNull(plan);
        Assert.Empty(plan.CharacterRefs);
        Assert.Empty(plan.AccountKeys);
        Assert.Empty(plan.CharacterKeys);
        Assert.Equal(string.Empty, plan.DiagnosticsReason);
    }

    [Fact]
    public void NullWarningEntriesAreRemoved()
    {
        const string json = """
            {
              "warnings": [null, "keep"],
              "participants": null,
              "leases": null,
              "stepResults": null
            }
            """;

        var result = DadIpcJson.Deserialize<DadRunResult>(json);

        Assert.NotNull(result);
        Assert.Equal(["keep"], result.Warnings);
        Assert.Empty(result.Participants);
        Assert.Empty(result.Leases);
        Assert.Empty(result.StepResults);
    }

    [Fact]
    public void NullStringDictionaryValuesNormalizeToEmpty()
    {
        var payload = DadIpcJson.Deserialize<StringDictionaryPayload>(
            "{\"text\":{\"missing\":null,\"keep\":\"value\"}}");

        Assert.NotNull(payload);
        Assert.Equal(string.Empty, payload.Text["missing"]);
        Assert.Equal("value", payload.Text["keep"]);
    }

    [Fact]
    public void HeartbeatRejectsMissingRequiredParticipant()
    {
        var accepted = DadIpcJson.TryDeserialize<DadHubHeartbeat>(
            "{\"participant\":null}",
            out var heartbeat,
            out var reason);

        Assert.False(accepted);
        Assert.Null(heartbeat);
        Assert.Contains("participant", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedNotificationDiagnosticsAreRateLimitedAndReportSuppression()
    {
        var gate = new DadBoundedDiagnosticGate(TimeSpan.FromMinutes(1));
        var start = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(gate.TryReport(start, out var firstSuppressed));
        Assert.Equal(0, firstSuppressed);
        Assert.False(gate.TryReport(start.AddSeconds(1), out _));
        Assert.False(gate.TryReport(start.AddSeconds(2), out _));
        Assert.True(gate.TryReport(start.AddMinutes(1), out var laterSuppressed));
        Assert.Equal(2, laterSuppressed);
    }

    [Theory]
    [InlineData("{\"plan\":null}")]
    [InlineData("{\"plan\":{\"request\":null,\"modules\":[]}}")]
    [InlineData("{\"plan\":{\"request\":{},\"modules\":null}}")]
    public void WorkerCommandsRejectMissingRequiredExecutionObjects(string json)
    {
        var accepted = DadIpcJson.TryDeserialize<DadWorkerExecutionCommand>(
            json,
            out var command,
            out var reason);

        Assert.False(accepted);
        Assert.Null(command);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void ProfileMutationRejectsMissingRequiredProfile()
    {
        var accepted = DadIpcJson.TryDeserialize<DadProfileUpdateRequest>(
            "{\"profile\":null}",
            out var request,
            out var reason);

        Assert.False(accepted);
        Assert.Null(request);
        Assert.Contains("profile", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrimaryLaunchSelectionAcceptsOmittedProfileButOrdinaryMutationDoesNot()
    {
        var accepted = DadIpcJson.TryDeserialize<DadProfileUpdateRequest>(
            "{\"updatePrimaryLaunchProfile\":true,\"profile\":null}",
            out var launchSelection,
            out var selectionReason);

        Assert.True(accepted, selectionReason);
        Assert.NotNull(launchSelection);
        Assert.True(launchSelection.UpdatePrimaryLaunchProfile);
        Assert.Null(launchSelection.Profile);

        Assert.False(DadIpcJson.TryDeserialize<DadProfileUpdateRequest>(
            "{\"updatePrimaryLaunchProfile\":false,\"profile\":null}",
            out _,
            out var mutationReason));
        Assert.Contains("profile", mutationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRequiredMutationObjectRejectsExplicitNull()
    {
        AssertRequiredObjectRejected<DadAssemblyInstructionDto>(
            "{\"frozenInviter\":null}",
            "inviter");
        AssertRequiredObjectRejected<DadLaunchProfileUpdateRequest>(
            "{\"profile\":null}",
            "profile");
        AssertRequiredObjectRejected<DadRosterAssignmentChangeRequest>(
            "{\"characterRef\":null}",
            "character");
        AssertRequiredObjectRejected<DadAggregateRosterCatalogRequest>(
            "{\"plan\":null}",
            "plan");
    }

    [Fact]
    public void RunResultCloneOwnsIndependentRequestGraph()
    {
        var original = new DadRunResult
        {
            Request = new DadRunRequest
            {
                RequestedBy = "original",
                Dungeon = new DadDungeonTask { Count = 7, SelectedDungeon = "test dungeon" },
                Msq = new DadMsqTask(),
                DutySupport = new DadDutySupportTask(),
                Trust = new DadTrustTask(),
                PremadeDuty = new DadPremadeDutyTask(),
                DailyMsq = new DadDailyMsqTask(),
                Blunderville = new DadBlundervilleTask(),
                Mogtome = new DadMogtomeTask(),
                Commendation = new DadCommendationTask(),
                Astrope = new DadAstropeTask(),
                CustomDuty = new DadCustomDutyTask(),
                Squadron = new DadSquadronTask(),
                VariantVvd = new DadVariantVvdTask(),
                LootGoblin = new DadLootGoblinTask(),
                CompletionActions = new DadCompletionActions { Commands = ["/test"] },
                ShoppingAssociations = [new DadShoppingRunAssociation { CreditedQuantities = new() { ["row"] = 1 } }],
                StopPolicy = new DadRunStopPolicy { ResolvedLevelTargets = [new DadResolvedLevelTarget { TargetLevel = 80 }] },
                Orchestration = new DadOrchestrationIntent
                {
                    RequiredCharacterKeys = [new DadCharacterKey("Alpha@World")],
                    PreferredRosterCharacters = [new DadRosterCharacterRef { RequiredJobId = 19 }],
                    RequiredRosterCharacters = [new DadRosterCharacterRef { RequiredJobId = 24 }],
                },
            },
        };

        var clone = original.Clone();
        Assert.Equal(DadIpcJson.Serialize(original), DadIpcJson.Serialize(clone));
        foreach (var property in typeof(DadRunRequest).GetProperties())
        {
            if (!property.PropertyType.IsValueType && property.PropertyType != typeof(string)
                && property.GetValue(original.Request) is { } value)
                Assert.NotSame(value, property.GetValue(clone.Request));
        }
        clone.Request!.RequestedBy = "clone";
        clone.Request.Orchestration.RequiredCharacterKeys[0] = new DadCharacterKey("Beta@World");
        clone.Request.Orchestration.PreferredRosterCharacters[0].RequiredJobId = 20;
        clone.Request.Orchestration.RequiredRosterCharacters[0].RequiredJobId = 21;
        clone.Request.Orchestration.RosterIntent.ExpectedPartySize = 8;
        clone.Request.Orchestration.WaitPolicy.AssemblyTimeoutSeconds = 999;
        clone.Request.CompletionActions!.Commands[0] = "/changed";
        clone.Request.ShoppingAssociations[0].CreditedQuantities!["row"] = 9;
        clone.Request.StopPolicy.ResolvedLevelTargets[0].TargetLevel = 90;
        clone.Request.PreDutyRepairPolicy.Enabled = !original.Request!.PreDutyRepairPolicy.Enabled;
        clone.Request.Dungeon!.Count = 99;
        clone.Request.DailyMsq!.QueueTarget.DisplayName = "changed";
        clone.Request.Commendation!.QueueTarget.DisplayName = "changed";
        clone.Request.Astrope!.QueueTarget.DisplayName = "changed";
        clone.Request.Astrope.ValidLocalTimeWindow.StartLocal = "12:00";
        clone.Request.CustomDuty!.QueueTarget.DisplayName = "changed";

        Assert.Equal("original", original.Request!.RequestedBy);
        Assert.Equal("Alpha@World", original.Request.Orchestration.RequiredCharacterKeys[0].Value);
        Assert.Equal(19u, original.Request.Orchestration.PreferredRosterCharacters[0].RequiredJobId);
        Assert.Equal(24u, original.Request.Orchestration.RequiredRosterCharacters[0].RequiredJobId);
        Assert.Equal(1, original.Request.Orchestration.RosterIntent.ExpectedPartySize);
        Assert.Equal(120, original.Request.Orchestration.WaitPolicy.AssemblyTimeoutSeconds);
        Assert.Equal("/test", original.Request.CompletionActions!.Commands[0]);
        Assert.Equal(1, original.Request.ShoppingAssociations[0].CreditedQuantities!["row"]);
        Assert.Equal(80, original.Request.StopPolicy.ResolvedLevelTargets[0].TargetLevel);
        Assert.NotEqual(original.Request.PreDutyRepairPolicy.Enabled, clone.Request.PreDutyRepairPolicy.Enabled);
        Assert.Equal(7, original.Request.Dungeon!.Count);
        Assert.Equal("Main Scenario Roulette", original.Request.DailyMsq!.QueueTarget.DisplayName);
        Assert.Equal("Under the Armour", original.Request.Commendation!.QueueTarget.DisplayName);
        Assert.Equal("Mentor Roulette", original.Request.Astrope!.QueueTarget.DisplayName);
        Assert.Equal("00:00", original.Request.Astrope.ValidLocalTimeWindow.StartLocal);
        Assert.Equal(string.Empty, original.Request.CustomDuty!.QueueTarget.DisplayName);
    }

    [Fact]
    public void CurrentValidPayloadRetainsSerializedShapeAcrossIngress()
    {
        var original = new DadRosterRefreshPlan
        {
            PlanId = "plan",
            CharacterKeys = [new DadCharacterKey("Alpha@World")],
            DiagnosticsReason = "test",
        };
        var json = DadIpcJson.Serialize(original);

        var restored = DadIpcJson.Deserialize<DadRosterRefreshPlan>(json);

        Assert.NotNull(restored);
        Assert.Equal(json, DadIpcJson.Serialize(restored));
    }

    public sealed class StringDictionaryPayload
    {
        public Dictionary<string, string> Text { get; set; } = [];
    }

    private static void AssertRequiredObjectRejected<T>(
        string json,
        string expectedReason)
    {
        Assert.False(DadIpcJson.TryDeserialize<T>(
            json,
            out var value,
            out var reason));
        Assert.Null(value);
        Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
    }
}
