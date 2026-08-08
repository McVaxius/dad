using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRouletteQueueRuntimeRulesTests
{
    private static readonly DateTime Start = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, true, 3u, 3u, true)]
    [InlineData(false, true, 3u, 3u, false)]
    [InlineData(true, false, 3u, 3u, false)]
    [InlineData(true, true, 2u, 3u, false)]
    [InlineData(true, true, 0u, 0u, false)]
    [InlineData(true, true, 3u, 0u, false)]
    [InlineData(true, true, 255u, 255u, true)]
    [InlineData(true, true, 256u, 256u, false)]
    public void SelectionProofRequiresExactRouletteTypeAndByteId(
        bool hasRouletteSelected,
        bool selectedContentIsRoulette,
        uint selectedId,
        uint requestedId,
        bool expected)
        => Assert.Equal(
            expected,
            DadRouletteSelectionProof.IsExact(
                hasRouletteSelected,
                selectedContentIsRoulette,
                selectedId,
                requestedId));

    [Fact]
    public void SettleDurationsRemainExact()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), DadRouletteQueueAttemptGate.SelectionSettle);
        Assert.Equal(TimeSpan.FromSeconds(8), DadRouletteQueueAttemptGate.RegistrationGrace);
    }

    [Fact]
    public void ExactPreexistingSelectionStillRequiresClearOpenMappedCallbackAndSixSecondProof()
    {
        var gate = new DadRouletteQueueAttemptGate();

        AssertDecision(
            gate.Decide(Start, exactRouletteSelected: true, registrationEvidenceObserved: false, stableMappingAvailable: false),
            DadRouletteQueueMutation.ClearSelection);
        Assert.Equal(0, gate.SelectionAttempts);

        AssertDecision(
            gate.Decide(Start, exactRouletteSelected: true, registrationEvidenceObserved: false, stableMappingAvailable: false),
            DadRouletteQueueMutation.OpenRoulette);
        Assert.Equal(0, gate.SelectionAttempts);

        AssertDecision(
            gate.Decide(Start, exactRouletteSelected: true, registrationEvidenceObserved: false, stableMappingAvailable: true),
            DadRouletteQueueMutation.SelectMappedEntry);
        Assert.Equal(1, gate.SelectionAttempts);

        var beforeSettle = gate.Decide(
            Start + TimeSpan.FromSeconds(5.999),
            exactRouletteSelected: true,
            registrationEvidenceObserved: false,
            stableMappingAvailable: true);
        AssertDecision(beforeSettle, DadRouletteQueueMutation.Wait);
        Assert.Contains("six seconds", beforeSettle.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gate.JoinAttempts);

        AssertDecision(
            gate.Decide(
                Start + DadRouletteQueueAttemptGate.SelectionSettle,
                exactRouletteSelected: true,
                registrationEvidenceObserved: false,
                stableMappingAvailable: true),
            DadRouletteQueueMutation.Join);
        Assert.Equal(1, gate.SelectionAttempts);
        Assert.Equal(1, gate.JoinAttempts);
    }

    [Fact]
    public void FailedSelectionProofKeepsStartingNewAttempts()
    {
        var gate = new DadRouletteQueueAttemptGate();

        AssertDecision(gate.Decide(Start, false, false, false), DadRouletteQueueMutation.ClearSelection);
        AssertDecision(gate.Decide(Start, false, false, false), DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(gate.Decide(Start, false, false, true), DadRouletteQueueMutation.SelectMappedEntry);

        var secondClear = gate.Decide(Start + TimeSpan.FromSeconds(6), false, false, true);
        AssertDecision(secondClear, DadRouletteQueueMutation.ClearSelection);
        AssertDecision(
            gate.Decide(Start + TimeSpan.FromSeconds(6), false, false, false),
            DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(
            gate.Decide(Start + TimeSpan.FromSeconds(6), false, false, true),
            DadRouletteQueueMutation.SelectMappedEntry);

        var thirdClear = gate.Decide(Start + TimeSpan.FromSeconds(12), false, false, true);
        AssertDecision(thirdClear, DadRouletteQueueMutation.ClearSelection);
        AssertDecision(
            gate.Decide(Start + TimeSpan.FromSeconds(12), false, false, false),
            DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(
            gate.Decide(Start + TimeSpan.FromSeconds(12), false, false, true),
            DadRouletteQueueMutation.SelectMappedEntry);

        var fourthClear = gate.Decide(Start + TimeSpan.FromSeconds(18), false, false, true);
        AssertDecision(fourthClear, DadRouletteQueueMutation.ClearSelection);
        Assert.Equal(3, gate.SelectionAttempts);
        Assert.Equal(0, gate.JoinAttempts);

        AssertDecision(
            gate.Decide(Start + TimeSpan.FromSeconds(18), false, false, false),
            DadRouletteQueueMutation.OpenRoulette);
        Assert.Equal(3, gate.SelectionAttempts);
        AssertDecision(
            gate.Decide(Start + TimeSpan.FromSeconds(18), false, false, true),
            DadRouletteQueueMutation.SelectMappedEntry);
        Assert.Equal(4, gate.SelectionAttempts);
    }

    [Fact]
    public void MissingOrUnstableLiveMappingCausesNoSelectionOrJoinMutation()
    {
        var gate = new DadRouletteQueueAttemptGate();

        AssertDecision(gate.Decide(Start, false, false, false), DadRouletteQueueMutation.ClearSelection);
        AssertDecision(gate.Decide(Start, false, false, false), DadRouletteQueueMutation.OpenRoulette);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var waiting = gate.Decide(Start.AddSeconds(attempt), true, false, false);
            AssertDecision(waiting, DadRouletteQueueMutation.Wait);
            Assert.Contains("stable", waiting.Reason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, gate.SelectionAttempts);
        Assert.Equal(0, gate.JoinAttempts);
        AssertDecision(gate.Decide(Start.AddSeconds(5), true, false, true), DadRouletteQueueMutation.SelectMappedEntry);
        Assert.Equal(1, gate.SelectionAttempts);
        Assert.Equal(0, gate.JoinAttempts);
    }

    [Fact]
    public void JoinRetriesUseFullEightSecondGraceAndRestartTheFullCycleIndefinitely()
    {
        var gate = AdvanceToFirstJoin();
        var firstJoinAt = Start + TimeSpan.FromSeconds(6);

        Assert.True(gate.IsRegistrationGraceActive(firstJoinAt + TimeSpan.FromSeconds(7.999)));
        AssertDecision(
            gate.Decide(firstJoinAt + TimeSpan.FromSeconds(7.999), true, false, true),
            DadRouletteQueueMutation.Wait);
        Assert.False(gate.IsRegistrationGraceActive(firstJoinAt + TimeSpan.FromSeconds(8)));

        AssertDecision(gate.Decide(firstJoinAt + TimeSpan.FromSeconds(8), true, false, false), DadRouletteQueueMutation.ClearSelection);
        AssertDecision(gate.Decide(firstJoinAt + TimeSpan.FromSeconds(8), true, false, false), DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(gate.Decide(firstJoinAt + TimeSpan.FromSeconds(8), true, false, true), DadRouletteQueueMutation.SelectMappedEntry);
        AssertDecision(
            gate.Decide(firstJoinAt + TimeSpan.FromSeconds(13.999), true, false, true),
            DadRouletteQueueMutation.Wait);

        AssertDecision(
            gate.Decide(firstJoinAt + TimeSpan.FromSeconds(14), true, false, true),
            DadRouletteQueueMutation.Join);
        Assert.Equal(2, gate.JoinAttempts);
        AssertDecision(
            gate.Decide(firstJoinAt + TimeSpan.FromSeconds(21.999), true, false, true),
            DadRouletteQueueMutation.Wait);

        AssertDecision(gate.Decide(firstJoinAt + TimeSpan.FromSeconds(22), true, false, false), DadRouletteQueueMutation.ClearSelection);
        AssertDecision(gate.Decide(firstJoinAt + TimeSpan.FromSeconds(22), true, false, false), DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(gate.Decide(firstJoinAt + TimeSpan.FromSeconds(22), true, false, true), DadRouletteQueueMutation.SelectMappedEntry);
        Assert.Equal(2, gate.JoinAttempts);
        Assert.Equal(3, gate.SelectionAttempts);
    }

    [Fact]
    public void RegistrationEvidenceSuppressesFurtherMutationEvenAfterGraceDeadline()
    {
        var gate = AdvanceToFirstJoin();

        var observed = gate.Decide(
            Start + TimeSpan.FromMinutes(1),
            exactRouletteSelected: false,
            registrationEvidenceObserved: true,
            stableMappingAvailable: false);

        AssertDecision(observed, DadRouletteQueueMutation.Wait);
        Assert.Contains("evidence observed", observed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, gate.JoinAttempts);
        Assert.Equal(1, gate.SelectionAttempts);

        AssertDecision(
            gate.Decide(Start + TimeSpan.FromMinutes(2), false, true, false),
            DadRouletteQueueMutation.Wait);
        Assert.Equal(1, gate.JoinAttempts);
    }

    [Fact]
    public void LostSelectionAfterRegistrationGraceForcesAnotherClearAndOpenProof()
    {
        var gate = AdvanceToFirstJoin();
        var graceExpiredAt = Start + TimeSpan.FromSeconds(14);

        AssertDecision(
            gate.Decide(graceExpiredAt, exactRouletteSelected: false, registrationEvidenceObserved: false, stableMappingAvailable: false),
            DadRouletteQueueMutation.ClearSelection);
        AssertDecision(
            gate.Decide(graceExpiredAt, exactRouletteSelected: false, registrationEvidenceObserved: false, stableMappingAvailable: false),
            DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(
            gate.Decide(graceExpiredAt, exactRouletteSelected: false, registrationEvidenceObserved: false, stableMappingAvailable: true),
            DadRouletteQueueMutation.SelectMappedEntry);
        Assert.Equal(2, gate.SelectionAttempts);
        Assert.Equal(1, gate.JoinAttempts);
        AssertDecision(
            gate.Decide(graceExpiredAt + TimeSpan.FromSeconds(5.999), true, false, true),
            DadRouletteQueueMutation.Wait);
        AssertDecision(
            gate.Decide(graceExpiredAt + TimeSpan.FromSeconds(6), true, false, true),
            DadRouletteQueueMutation.Join);
        Assert.Equal(2, gate.JoinAttempts);
    }

    [Fact]
    public void ResetClearsCountersDeadlinesAndPendingMutations()
    {
        var gate = AdvanceToFirstJoin();
        Assert.True(gate.IsRegistrationGraceActive(Start + TimeSpan.FromSeconds(7)));

        gate.Reset();

        Assert.Equal(0, gate.SelectionAttempts);
        Assert.Equal(0, gate.JoinAttempts);
        Assert.False(gate.IsRegistrationGraceActive(Start + TimeSpan.FromSeconds(7)));
        AssertDecision(
            gate.Decide(Start + TimeSpan.FromMinutes(5), true, false, true),
            DadRouletteQueueMutation.ClearSelection);
    }

    [Fact]
    public void TerritoryCannotBeCapturedWithoutRunSpecificEntryEvidence()
    {
        var gate = new DadRouletteTerritoryEvidenceGate();

        Assert.False(gate.TryCapture(boundByDuty: true, territoryId: 777));
        Assert.False(gate.TryCapture(boundByDuty: false, territoryId: 777));
        Assert.False(gate.TryCapture(boundByDuty: true, territoryId: 0));
        Assert.False(gate.EntryEvidenceObserved);
        Assert.Equal((uint)0, gate.CapturedTerritoryId);

        gate.ObserveEntryEvidence();
        Assert.True(gate.EntryEvidenceObserved);
        Assert.False(gate.TryCapture(boundByDuty: false, territoryId: 777));
        Assert.False(gate.TryCapture(boundByDuty: true, territoryId: 0));
        Assert.Equal((uint)0, gate.CapturedTerritoryId);
    }

    [Fact]
    public void VerifiedExactJoinAllowsDirectBoundTerritoryCapture()
    {
        const uint capturedTerritory = 777;
        var gate = new DadRouletteTerritoryEvidenceGate();

        gate.MarkVerifiedExactJoin();

        Assert.True(gate.TryCapture(boundByDuty: true, capturedTerritory));
        Assert.Equal(capturedTerritory, gate.CapturedTerritoryId);
    }

    [Fact]
    public void VerifiedExactJoinDoesNotSurviveResetOrNewSelectionCycle()
    {
        var gate = new DadRouletteTerritoryEvidenceGate();

        gate.MarkVerifiedExactJoin();
        gate.Reset();
        Assert.False(gate.TryCapture(boundByDuty: true, territoryId: 777));

        gate.MarkVerifiedExactJoin();
        gate.ClearVerifiedExactJoin();
        Assert.False(gate.TryCapture(boundByDuty: true, territoryId: 777));
    }

    [Fact]
    public void FirstEvidenceBackedBoundTerritoryIsStableForEntryCompletionAndExit()
    {
        const uint capturedTerritory = 777;
        var gate = new DadRouletteTerritoryEvidenceGate();
        gate.ObserveEntryEvidence();

        Assert.True(gate.TryCapture(boundByDuty: true, capturedTerritory));
        Assert.Equal(capturedTerritory, gate.CapturedTerritoryId);
        Assert.True(gate.TryCapture(boundByDuty: true, capturedTerritory));
        Assert.False(gate.TryCapture(boundByDuty: true, territoryId: 778));
        Assert.Equal(capturedTerritory, gate.CapturedTerritoryId);

        Assert.True(gate.IsInCapturedDuty(boundByDuty: true, capturedTerritory));
        Assert.False(gate.IsInCapturedDuty(boundByDuty: true, territoryId: 778));
        Assert.False(gate.IsInCapturedDuty(boundByDuty: false, capturedTerritory));
        Assert.False(gate.IsInCapturedDuty(boundByDuty: false, territoryId: 778));

        Assert.True(gate.MatchesCompletion(capturedTerritory, Start, Start));
        Assert.True(gate.MatchesCompletion(capturedTerritory, Start.AddMinutes(20), Start));
        Assert.False(gate.MatchesCompletion(778, Start.AddMinutes(20), Start));
        Assert.False(gate.MatchesCompletion(capturedTerritory, Start.AddTicks(-1), Start));

        // Completion truth remains tied to the captured territory after the exit predicate turns false.
        Assert.False(gate.IsInCapturedDuty(boundByDuty: false, territoryId: 999));
        Assert.True(gate.MatchesCompletion(capturedTerritory, Start.AddMinutes(20), Start));
    }

    [Fact]
    public void TerritoryResetDropsEntryCompletionAndCaptureTruth()
    {
        var gate = new DadRouletteTerritoryEvidenceGate();
        gate.ObserveEntryEvidence();
        Assert.True(gate.TryCapture(true, 777));

        gate.Reset();

        Assert.False(gate.EntryEvidenceObserved);
        Assert.Equal((uint)0, gate.CapturedTerritoryId);
        Assert.False(gate.IsInCapturedDuty(true, 777));
        Assert.False(gate.MatchesCompletion(777, Start.AddMinutes(1), Start));
        Assert.False(gate.TryCapture(true, 777));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnrestrictedLeaseRestoresTheCapturedPreviousValue(bool previousValue)
    {
        var lease = new DadUnrestrictedPartyOverrideLease();
        var currentValue = previousValue;
        var writes = new List<bool>();

        Assert.True(lease.Ensure(
            requiredValue: false,
            read: () => currentValue,
            write: value =>
            {
                writes.Add(value);
                currentValue = value;
            },
            out var changed,
            out var ensureFailure), ensureFailure);

        Assert.True(lease.IsActive);
        Assert.Equal(previousValue, lease.PreviousValue);
        Assert.Equal(previousValue, changed);
        Assert.False(currentValue);
        Assert.Equal(previousValue ? new[] { false } : Array.Empty<bool>(), writes);

        Assert.True(lease.Restore(
            read: () => currentValue,
            write: value =>
            {
                writes.Add(value);
                currentValue = value;
            },
            out var restoreFailure), restoreFailure);

        Assert.False(lease.IsActive);
        Assert.Equal(previousValue, currentValue);
        Assert.Equal(previousValue ? new[] { false, true } : Array.Empty<bool>(), writes);
    }

    [Fact]
    public void RepeatedEnsureDoesNotOverwriteTheOriginalPreviousValue()
    {
        var lease = new DadUnrestrictedPartyOverrideLease();
        var currentValue = true;

        Assert.True(lease.Ensure(false, () => currentValue, value => currentValue = value, out _, out _));
        Assert.False(currentValue);
        Assert.True(lease.Ensure(false, () => currentValue, value => currentValue = value, out var changed, out _));

        Assert.False(changed);
        Assert.True(lease.PreviousValue);
        Assert.True(lease.Restore(() => currentValue, value => currentValue = value, out _));
        Assert.True(currentValue);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void RestoreReadFailureRetainsActiveLeaseForRetry()
    {
        var lease = ActiveLeaseWithPreviousTrue(out var state);

        Assert.False(lease.Restore(
            read: () => throw new InvalidOperationException("read failed"),
            write: value => state.Value = value,
            out var failure));

        Assert.Equal("read failed", failure);
        Assert.True(lease.IsActive);
        Assert.True(lease.PreviousValue);

        Assert.True(lease.Restore(() => state.Value, value => state.Value = value, out failure), failure);
        Assert.True(state.Value);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void RestoreWriteFailureRetainsActiveLeaseForRetry()
    {
        var lease = ActiveLeaseWithPreviousTrue(out var state);

        Assert.False(lease.Restore(
            read: () => state.Value,
            write: _ => throw new InvalidOperationException("write failed"),
            out var failure));

        Assert.Equal("write failed", failure);
        Assert.False(state.Value);
        Assert.True(lease.IsActive);
        Assert.True(lease.PreviousValue);

        Assert.True(lease.Restore(() => state.Value, value => state.Value = value, out failure), failure);
        Assert.True(state.Value);
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void InactiveRestoreIsIdempotentAndDoesNotTouchGateway()
    {
        var lease = new DadUnrestrictedPartyOverrideLease();
        var reads = 0;
        var writes = 0;

        Assert.True(lease.Restore(
            read: () =>
            {
                reads++;
                return true;
            },
            write: _ => writes++,
            out var failure), failure);

        Assert.Equal(0, reads);
        Assert.Equal(0, writes);
        Assert.False(lease.IsActive);
    }

    private static DadRouletteQueueAttemptGate AdvanceToFirstJoin()
    {
        var gate = new DadRouletteQueueAttemptGate();
        AssertDecision(gate.Decide(Start, false, false, false), DadRouletteQueueMutation.ClearSelection);
        AssertDecision(gate.Decide(Start, false, false, false), DadRouletteQueueMutation.OpenRoulette);
        AssertDecision(gate.Decide(Start, false, false, true), DadRouletteQueueMutation.SelectMappedEntry);
        AssertDecision(
            gate.Decide(Start + DadRouletteQueueAttemptGate.SelectionSettle, true, false, true),
            DadRouletteQueueMutation.Join);
        return gate;
    }

    private static DadUnrestrictedPartyOverrideLease ActiveLeaseWithPreviousTrue(out MutableBool state)
    {
        state = new MutableBool { Value = true };
        var lease = new DadUnrestrictedPartyOverrideLease();
        var capturedState = state;
        Assert.True(lease.Ensure(
            false,
            () => capturedState.Value,
            value => capturedState.Value = value,
            out var changed,
            out var failure), failure);
        Assert.True(changed);
        Assert.False(state.Value);
        return lease;
    }

    private static void AssertDecision(DadRouletteQueueDecision decision, DadRouletteQueueMutation expected)
        => Assert.Equal(expected, decision.Mutation);

    private sealed class MutableBool
    {
        public bool Value { get; set; }
    }
}
