using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadVermaxionHandoffTests
{
    private static readonly DateTime Now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Pending", DadVermaxionReservationState.Pending, true, false)]
    [InlineData("Granting", DadVermaxionReservationState.Granting, true, false)]
    [InlineData("Granted", DadVermaxionReservationState.Granted, false, true)]
    [InlineData("Released", DadVermaxionReservationState.Released, false, false)]
    [InlineData("Rejected", DadVermaxionReservationState.Rejected, false, false)]
    public void ParsesTypedV2States(string state, DadVermaxionReservationState expected, bool waits, bool granted)
    {
        var json = $$"""
        {"version":2,"operationToken":"op","state":"{{state}}","vermaxionActivity":"Idle","vermaxionState":"Idle","autoRetainerBusyKnown":true,"autoRetainerBusy":false,"multiModeKnown":true,"multiModeEnabled":false,"updatedAtUtc":"2026-07-11T11:59:59Z","summary":"ok"}
        """;

        var result = DadVermaxionReservationParser.Parse(json, Now);

        Assert.Equal(expected, result.State);
        Assert.Equal(waits, result.RequiresWait);
        Assert.Equal(granted, result.IsGranted);
        Assert.Equal(DadVermaxionReservationWireFormat.CanonicalString, result.WireFormat);
    }

    [Theory]
    [InlineData(0, DadVermaxionReservationState.Pending, true, false)]
    [InlineData(1, DadVermaxionReservationState.Granting, true, false)]
    [InlineData(2, DadVermaxionReservationState.Granted, false, true)]
    [InlineData(3, DadVermaxionReservationState.Released, false, false)]
    [InlineData(4, DadVermaxionReservationState.Rejected, false, false)]
    public void ParsesLegacyNumericV2States(
        int numericState,
        DadVermaxionReservationState expected,
        bool waits,
        bool granted)
    {
        var json = $$"""
        {"version":2,"operationToken":"op","state":{{numericState}},"summary":"legacy"}
        """;

        var result = DadVermaxionReservationParser.Parse(json, Now);

        Assert.Equal(expected, result.State);
        Assert.Equal(waits, result.RequiresWait);
        Assert.Equal(granted, result.IsGranted);
        Assert.Equal(DadVermaxionReservationWireFormat.LegacyNumeric, result.WireFormat);
    }

    [Fact]
    public void MalformedV2IsUnavailable()
    {
        var result = DadVermaxionReservationParser.Parse("not json", Now);

        Assert.Equal(DadVermaxionReservationState.Unavailable, result.State);
        Assert.True(result.UsesLegacyBoundary);
        Assert.False(result.CompatibilityFallbackEligible);
    }

    [Theory]
    [InlineData("Released")]
    [InlineData("Rejected")]
    public void ExactTokenTerminalReleaseResponseProvesNoOwnedReservation(string state)
    {
        var status = DadVermaxionReservationParser.Parse(
            $$"""{"version":2,"operationToken":"operation-a","state":"{{state}}"}""",
            Now);

        Assert.True(DadVermaxionReleaseProofRules.ProvesNoOwnedReservation(status, "OPERATION-A"));
    }

    [Theory]
    [InlineData("Released", "")]
    [InlineData("Rejected", "")]
    [InlineData("Released", "another-operation")]
    [InlineData("Rejected", "another-operation")]
    [InlineData("Pending", "operation-a")]
    public void MissingMismatchedOrNonterminalReleaseResponseDoesNotProveCleanup(
        string state,
        string responseToken)
    {
        var status = DadVermaxionReservationParser.Parse(
            $$"""{"version":2,"operationToken":"{{responseToken}}","state":"{{state}}"}""",
            Now);

        Assert.False(DadVermaxionReleaseProofRules.ProvesNoOwnedReservation(status, "operation-a"));
    }

    [Fact]
    public void UnavailableReleaseResponseDoesNotProveCleanup()
    {
        var status = DadVermaxionReservationParser.Parse(null, Now, "release channel unavailable");

        Assert.False(DadVermaxionReleaseProofRules.ProvesNoOwnedReservation(status, "operation-a"));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("\"0\"")]
    [InlineData("\"Unavailable\"")]
    [InlineData("\"unknown\"")]
    public void UnknownNumericOrStringV2StatesFailClosed(string rawState)
    {
        var result = DadVermaxionReservationParser.Parse(
            $$"""{"version":2,"operationToken":"op","state":{{rawState}}}""",
            Now);

        Assert.Equal(DadVermaxionReservationState.Unavailable, result.State);
        Assert.Equal(DadVermaxionReservationWireFormat.Unavailable, result.WireFormat);
        Assert.False(result.CompatibilityFallbackEligible);
    }

    [Fact]
    public void FreshGrantOverridesStaleLegacyBusyStatus()
    {
        var reservation = DadVermaxionReservationParser.Parse(
            """{"version":2,"operationToken":"op","state":"Granted","vermaxionActivity":"DadHandoff","vermaxionState":"Granted","summary":"fresh grant"}""",
            Now);
        var staleLegacy = DadVermaxionStatusParser.Parse(
            true,
            """{"version":1,"isBusy":true,"activity":"OldWork","state":"Busy","summary":"stale busy"}""",
            Now);

        var view = DadVermaxionAuthorityRules.Resolve("op", reservation, staleLegacy);

        Assert.True(view.Authoritative);
        Assert.False(view.Held);
        Assert.Equal(DadVermaxionMutationAuthorization.Granted, view.MutationAuthorization);
        Assert.Equal("DadHandoff", view.Activity);
        Assert.Equal("fresh grant", view.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedReservationWithTokenFallsBackToFreshLegacyTruth(bool legacyBusy)
    {
        var released = DadVermaxionReservationParser.Parse(
            """{"version":2,"operationToken":"op","state":"Released","summary":"released"}""",
            Now);
        var legacy = DadVermaxionStatusParser.Parse(
            true,
            $$"""{"version":1,"isBusy":{{legacyBusy.ToString().ToLowerInvariant()}},"activity":"{{(legacyBusy ? "NewWork" : "Idle")}}","state":"{{(legacyBusy ? "Busy" : "Idle")}}"}""",
            Now);

        var view = DadVermaxionAuthorityRules.Resolve("op", released, legacy);

        Assert.False(released.IsAuthoritativeFor("op"));
        Assert.False(view.Authoritative);
        Assert.Equal(legacyBusy, view.Held);
        Assert.Equal(legacyBusy ? "NewWork" : "Idle", view.Activity);
        Assert.Equal(DadVermaxionMutationAuthorization.None, view.MutationAuthorization);
    }

    [Theory]
    [InlineData(DadVermaxionReservationState.Pending)]
    [InlineData(DadVermaxionReservationState.Granting)]
    public void ActiveNonGrantedV2ReservationIsAuthoritativeAndFailClosed(
        DadVermaxionReservationState state)
    {
        var reservation = new DadVermaxionReservationStatus
        {
            OperationToken = "op",
            State = state,
            VermaxionActivity = "CurrentActivity",
            VermaxionState = "CurrentState",
            Summary = "exact v2 reason",
        };
        var idleLegacy = DadVermaxionStatusParser.Parse(
            true,
            """{"version":1,"isBusy":false,"activity":"Idle","state":"Idle","summary":"legacy idle"}""",
            Now);

        var view = DadVermaxionAuthorityRules.Resolve(
            "op",
            reservation,
            idleLegacy,
            new DadVermaxionCompatibilityEvidence(true, true, true, true));

        Assert.True(view.Authoritative);
        Assert.True(view.Held);
        Assert.Equal("CurrentActivity", view.Activity);
        Assert.Equal("CurrentState", view.State);
        Assert.Equal("exact v2 reason", view.Summary);
        Assert.Equal(DadVermaxionMutationAuthorization.None, view.MutationAuthorization);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Granting")]
    public void ParsedActiveV2ReservationNeverUsesCompatibilityFallback(string state)
    {
        var reservation = DadVermaxionReservationParser.Parse(
            $$"""{"version":2,"operationToken":"op","state":"{{state}}"}""",
            Now);

        var view = DadVermaxionAuthorityRules.Resolve(
            "op",
            reservation,
            DadVermaxionStatusParser.Parse(
                true,
                """{"version":1,"isBusy":false,"activity":"Idle","state":"Idle"}""",
                Now),
            new DadVermaxionCompatibilityEvidence(true, true, true, true));

        Assert.True(view.Authoritative);
        Assert.True(view.Held);
        Assert.Equal(DadVermaxionMutationAuthorization.None, view.MutationAuthorization);
    }

    [Fact]
    public void MalformedV2DoesNotUseCompatibilityFallback()
    {
        var reservation = DadVermaxionReservationParser.BindToRequest(
            DadVermaxionReservationParser.Parse("not json", Now),
            new DadVermaxionReservationRequest { OperationToken = "op" });

        var view = DadVermaxionAuthorityRules.Resolve(
            "op",
            reservation,
            DadVermaxionStatusParser.Parse(
                true,
                """{"version":1,"isBusy":false,"activity":"Idle","state":"Idle"}""",
                Now),
            new DadVermaxionCompatibilityEvidence(true, true, true, true));

        Assert.True(view.Authoritative);
        Assert.True(view.Held);
        Assert.Equal(DadVermaxionMutationAuthorization.None, view.MutationAuthorization);
    }

    [Fact]
    public void UnavailableV2UsesCompleteVerifiedIdleCompatibilityEvidence()
    {
        var reservation = UnavailableReservation();
        var idleLegacy = DadVermaxionStatusParser.Parse(
            true,
            """{"version":1,"isBusy":false,"activity":"Idle","state":"Idle","summary":"legacy idle"}""",
            Now);

        var view = DadVermaxionAuthorityRules.Resolve(
            "op",
            reservation,
            idleLegacy,
            new DadVermaxionCompatibilityEvidence(true, true, true, true));

        Assert.True(view.Authoritative);
        Assert.False(view.Held);
        Assert.Equal(DadVermaxionMutationAuthorization.CompatibilityIdle, view.MutationAuthorization);
        Assert.Equal("CompatibilityHandoff", view.Activity);
        Assert.Equal("Compatibility handoff: VERMAXION idle / AR idle", view.Summary);
    }

    [Fact]
    public void GenuinelyUnavailableV2IpcRemainsCompatibilityEligible()
    {
        var unavailable = DadVermaxionReservationParser.BindToRequest(
            DadVermaxionReservationParser.Parse(null, Now, "IPC channel unavailable"),
            new DadVermaxionReservationRequest { OperationToken = "op" });

        Assert.Equal(DadVermaxionReservationState.Unavailable, unavailable.State);
        Assert.True(unavailable.CompatibilityFallbackEligible);
        Assert.True(unavailable.IsAuthoritativeFor("op"));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void UnavailableV2FailsClosedWhenAnyCompatibilityEvidenceIsMissing(
        bool vermaxionIdle,
        bool autoRetainerReadableIdle,
        bool multiModeDisabled,
        bool suppressionReadableAndAvailable)
    {
        var view = DadVermaxionAuthorityRules.Resolve(
            "op",
            UnavailableReservation(),
            DadVermaxionStatusParser.Parse(true, null, Now, "unreadable"),
            new DadVermaxionCompatibilityEvidence(
                vermaxionIdle,
                autoRetainerReadableIdle,
                multiModeDisabled,
                suppressionReadableAndAvailable));

        Assert.True(view.Held);
        Assert.Equal(DadVermaxionMutationAuthorization.None, view.MutationAuthorization);
    }

    [Fact]
    public void RepeatedReloadInvalidationPreservesLogicalRequestIdentity()
    {
        var request = new DadVermaxionReservationRequest
        {
            OperationToken = "op",
            SchedulerRunId = "run",
            SlotId = "Slot1",
            AccountKey = "account",
            CharacterKey = "Target Character@World",
            RequestedAtUtc = Now.AddMinutes(-1),
        };

        var first = DadVermaxionReservationParser.Renewing(request, Now);
        var second = DadVermaxionReservationParser.Renewing(request, Now.AddSeconds(5), "IPC missing");

        Assert.True(first.IsAuthoritativeFor("op"));
        Assert.True(second.IsAuthoritativeFor("op"));
        Assert.Equal(DadVermaxionReservationState.Unavailable, first.State);
        Assert.Equal("Target Character@World", second.CharacterKey);
        Assert.Contains("renewing handoff", second.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static DadVermaxionReservationStatus UnavailableReservation()
        => new()
        {
            OperationToken = "op",
            State = DadVermaxionReservationState.Unavailable,
            CompatibilityFallbackEligible = true,
            VermaxionActivity = "ReservationRenewal",
            VermaxionState = "Unavailable",
            Summary = "v2 unavailable",
        };
}
