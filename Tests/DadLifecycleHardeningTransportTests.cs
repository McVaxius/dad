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
                Orchestration = new DadOrchestrationIntent
                {
                    RequiredCharacterKeys = [new DadCharacterKey("Alpha@World")],
                },
            },
        };

        var clone = original.Clone();
        clone.Request!.RequestedBy = "clone";
        clone.Request.Orchestration.RequiredCharacterKeys[0] = new DadCharacterKey("Beta@World");

        Assert.Equal("original", original.Request!.RequestedBy);
        Assert.Equal("Alpha@World", original.Request.Orchestration.RequiredCharacterKeys[0].Value);
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
