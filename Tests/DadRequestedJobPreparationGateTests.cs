using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadRequestedJobPreparationGateTests
{
    private static readonly DateTime Start = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GearsetSelectionUsesLowestExistingValidExactJob()
    {
        var gearsetId = DadClassJobGearsetSelectionRules.SelectFirstMatching(
            [
                new DadClassJobGearsetSnapshot(22, 21, Exists: true, IsValid: true),
                new DadClassJobGearsetSnapshot(3, 21, Exists: true, IsValid: false),
                new DadClassJobGearsetSnapshot(12, 19, Exists: true, IsValid: true),
                new DadClassJobGearsetSnapshot(7, 21, Exists: false, IsValid: true),
                new DadClassJobGearsetSnapshot(9, 21, Exists: true, IsValid: true),
            ],
            requiredJobId: 21);

        Assert.Equal(9, gearsetId);
    }

    [Fact]
    public void AnyJobIsTerminalWithoutMutation()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key(requiredJobId: null);
        var calls = 0;

        var proof = gate.Advance(
            key,
            Observation(key, currentJobId: 19),
            Start,
            _ =>
            {
                calls++;
                return DadClassJobEquipAttemptResult.Success();
            });

        Assert.Equal(DadRequestedJobPreparationStatus.NotRequested, proof.Status);
        Assert.Equal(0, calls);
        Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(proof, key));
    }

    [Fact]
    public void AlreadyMatchedIsIdempotentWithoutGearsetLookupOrMutation()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var calls = 0;
        var observation = new DadRequestedJobPreparationObservation(
            key,
            CurrentJobId: 21,
            IsSafeToEquip: true,
            GearsetCatalog: null);

        var first = gate.Advance(key, observation, Start, _ =>
        {
            calls++;
            return DadClassJobEquipAttemptResult.Success();
        });
        var repeated = gate.Advance(key, observation, Start.AddSeconds(1), _ =>
        {
            calls++;
            return DadClassJobEquipAttemptResult.Success();
        });

        Assert.Equal(DadRequestedJobPreparationStatus.AlreadyMatched, first.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.AlreadyMatched, repeated.Status);
        Assert.Equal(0, calls);
        Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(repeated, key));
    }

    [Fact]
    public void AcceptedFirstGearsetWaitsForAndThenVerifiesJob()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var calls = new List<int>();

        var awaiting = gate.Advance(key, Observation(key), Start, gearsetId =>
        {
            calls.Add(gearsetId);
            return DadClassJobEquipAttemptResult.Success();
        });
        var switched = gate.Advance(
            key,
            Observation(key, currentJobId: 21),
            Start.AddSeconds(1),
            _ => throw new InvalidOperationException("must not equip twice"));

        Assert.Equal(DadRequestedJobPreparationStatus.AwaitingVerification, awaiting.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.Switched, switched.Status);
        Assert.Equal([4], calls);
        Assert.Equal(1, switched.AttemptCount);
        Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(switched, key));
    }

    [Fact]
    public void MissingMatchingGearsetSoftFailsImmediately()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var catalog = DadClassJobGearsetCatalogSnapshot.Success(
            [new DadClassJobGearsetSnapshot(2, 19, Exists: true, IsValid: true)]);

        var proof = gate.Advance(
            key,
            Observation(key, catalog: catalog),
            Start,
            _ => throw new InvalidOperationException("must not mutate"));

        Assert.Equal(DadRequestedJobPreparationStatus.SoftFailed, proof.Status);
        Assert.Equal(0, proof.AttemptCount);
        Assert.Contains("No valid saved gearset", proof.FailureReason);
        Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(proof, key));
    }

    [Fact]
    public void OnlyAllowedTerminalPreparationOutcomesPermitPartyReadiness()
    {
        var key = Key();

        Assert.False(DadRequestedJobPreparationProofRules.PermitsReadiness(null, key, currentJobId: 21));
        Assert.False(Permits(DadRequestedJobPreparationStatus.Pending, currentJobId: 19));
        Assert.False(Permits(DadRequestedJobPreparationStatus.AwaitingVerification, currentJobId: 19));
        Assert.False(Permits(DadRequestedJobPreparationStatus.Cancelled, currentJobId: 21));
        Assert.False(Permits(DadRequestedJobPreparationStatus.NotRequested, currentJobId: 21));
        Assert.True(Permits(DadRequestedJobPreparationStatus.AlreadyMatched, currentJobId: 21));
        Assert.True(Permits(DadRequestedJobPreparationStatus.Switched, currentJobId: 21));
        Assert.True(Permits(DadRequestedJobPreparationStatus.SoftFailed, currentJobId: 19));
        Assert.False(Permits(DadRequestedJobPreparationStatus.Switched, currentJobId: 19));

        bool Permits(DadRequestedJobPreparationStatus status, uint currentJobId)
            => DadRequestedJobPreparationProofRules.PermitsReadiness(
                new DadRequestedJobPreparationProof { Key = key, Status = status },
                key,
                currentJobId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OnlyActualMutationCallsConsumeTheThreeAttemptAllowance(
        bool catalogAvailable,
        bool safeToEquip)
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var calls = 0;
        DadRequestedJobPreparationProof? proof = null;

        for (var second = 0; second < 3; second++)
        {
            var catalog = catalogAvailable
                ? Catalog()
                : DadClassJobGearsetCatalogSnapshot.Unavailable("module unavailable");
            proof = gate.Advance(
                key,
                Observation(key, isSafeToEquip: safeToEquip, catalog: catalog),
                Start.AddSeconds(second),
                _ =>
                {
                    calls++;
                    if (catalogAvailable && safeToEquip)
                        throw new InvalidOperationException("native exception");
                    return DadClassJobEquipAttemptResult.Rejected("native -1");
                });

            if (!catalogAvailable || !safeToEquip || second < 2)
                Assert.Equal(DadRequestedJobPreparationStatus.Pending, proof.Status);
        }

        Assert.NotNull(proof);
        Assert.Equal(
            catalogAvailable && safeToEquip
                ? DadRequestedJobPreparationStatus.SoftFailed
                : DadRequestedJobPreparationStatus.Pending,
            proof!.Status);
        Assert.Equal(catalogAvailable && safeToEquip ? 3 : 0, proof.AttemptCount);
        Assert.Equal(catalogAvailable && safeToEquip ? 3 : 0, calls);
    }

    [Fact]
    public void RetryDoesNotRunBeforeOneSecond()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var calls = 0;

        DadClassJobEquipAttemptResult Reject(int _)
        {
            calls++;
            return DadClassJobEquipAttemptResult.Rejected("native -1");
        }

        gate.Advance(key, Observation(key), Start, Reject);
        var early = gate.Advance(key, Observation(key), Start.AddMilliseconds(999), Reject);
        gate.Advance(key, Observation(key), Start.AddSeconds(1), Reject);

        Assert.Equal(2, calls);
        Assert.Equal(1, early.AttemptCount);
    }

    [Fact]
    public void CatalogReadIsDueOnlyForSafePendingRetry()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var safe = Observation(key) with { GearsetCatalog = null };
        var unsafeObservation = safe with { IsSafeToEquip = false, UnsafeReason = "unsafe" };

        Assert.True(gate.NeedsGearsetCatalog(key, safe, Start));
        Assert.False(gate.NeedsGearsetCatalog(key, unsafeObservation, Start));

        gate.Advance(
            key,
            Observation(key),
            Start,
            _ => DadClassJobEquipAttemptResult.Rejected("native -1"));

        Assert.False(gate.NeedsGearsetCatalog(key, safe, Start.AddMilliseconds(999)));
        Assert.True(gate.NeedsGearsetCatalog(key, safe, Start.AddSeconds(1)));
    }

    [Fact]
    public void CatalogReadIsNeverDueDuringVerificationOrAfterTerminalResult()
    {
        var key = Key();
        var observationWithoutCatalog = Observation(key) with { GearsetCatalog = null };

        var awaitingGate = new DadRequestedJobPreparationGate();
        awaitingGate.Advance(key, Observation(key), Start, _ => DadClassJobEquipAttemptResult.Success());
        Assert.False(awaitingGate.NeedsGearsetCatalog(key, observationWithoutCatalog, Start.AddSeconds(1)));

        var terminalGate = new DadRequestedJobPreparationGate();
        terminalGate.Advance(
            key,
            Observation(
                key,
                catalog: DadClassJobGearsetCatalogSnapshot.Success(
                    [new DadClassJobGearsetSnapshot(1, 19, Exists: true, IsValid: true)])),
            Start,
            _ => throw new InvalidOperationException("must not equip"));
        Assert.False(terminalGate.NeedsGearsetCatalog(key, observationWithoutCatalog, Start.AddSeconds(1)));
    }

    [Fact]
    public void NativeMinusOneStyleRejectionSoftFailsOnThirdActualCall()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var calls = 0;
        DadRequestedJobPreparationProof? proof = null;

        for (var second = 0; second < 3; second++)
        {
            proof = gate.Advance(key, Observation(key), Start.AddSeconds(second), _ =>
            {
                calls++;
                return DadClassJobEquipAttemptResult.Rejected("EquipGearset returned -1");
            });
        }

        Assert.NotNull(proof);
        Assert.Equal(DadRequestedJobPreparationStatus.SoftFailed, proof!.Status);
        Assert.Equal(3, proof.AttemptCount);
        Assert.Equal(3, calls);
        Assert.Contains("returned -1", proof.FailureReason);
    }

    [Fact]
    public void AcceptedEquipReturnsToRetryAndSoftFailsAfterThirdUnverifiedCall()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();

        gate.Advance(key, Observation(key), Start, _ => DadClassJobEquipAttemptResult.Success());
        var early = gate.Advance(
            key,
            Observation(key),
            Start.AddMilliseconds(4999),
            _ => throw new InvalidOperationException("must not equip twice"));
        var firstTimedOut = gate.Advance(
            key,
            Observation(key),
            Start.AddSeconds(5),
            _ => throw new InvalidOperationException("timeout transition must not equip"));

        var secondAwaiting = gate.Advance(key, Observation(key), Start.AddSeconds(6), _ => DadClassJobEquipAttemptResult.Success());
        var secondTimedOut = gate.Advance(key, Observation(key), Start.AddSeconds(11), _ => throw new InvalidOperationException("timeout transition must not equip"));
        var thirdAwaiting = gate.Advance(key, Observation(key), Start.AddSeconds(12), _ => DadClassJobEquipAttemptResult.Success());
        var final = gate.Advance(key, Observation(key), Start.AddSeconds(17), _ => throw new InvalidOperationException("timeout transition must not equip"));

        Assert.Equal(DadRequestedJobPreparationStatus.AwaitingVerification, early.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.Pending, firstTimedOut.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.AwaitingVerification, secondAwaiting.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.Pending, secondTimedOut.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.AwaitingVerification, thirdAwaiting.Status);
        Assert.Equal(DadRequestedJobPreparationStatus.SoftFailed, final.Status);
        Assert.Equal(3, final.AttemptCount);
        Assert.Contains("not observed within five seconds", final.FailureReason);
    }

    [Theory]
    [InlineData("run-other", "worker-1", "Slot1", "account-1", "Player One@Excalibur", 1234ul, 21u)]
    [InlineData("run-1", "worker-other", "Slot1", "account-1", "Player One@Excalibur", 1234ul, 21u)]
    [InlineData("run-1", "worker-1", "Slot2", "account-1", "Player One@Excalibur", 1234ul, 21u)]
    [InlineData("run-1", "worker-1", "Slot1", "account-other", "Player One@Excalibur", 1234ul, 21u)]
    [InlineData("run-1", "worker-1", "Slot1", "account-1", "Player Two@Excalibur", 1234ul, 21u)]
    [InlineData("run-1", "worker-1", "Slot1", "account-1", "Player One@Excalibur", 9999ul, 21u)]
    [InlineData("run-1", "worker-1", "Slot1", "account-1", "Player One@Excalibur", 1234ul, 19u)]
    public void AnyExactIdentityDriftWaitsWithoutMutationOrAttemptConsumption(
        string runId,
        string workerSessionId,
        string slotId,
        string accountKey,
        string characterKey,
        ulong contentId,
        uint requiredJobId)
    {
        var gate = new DadRequestedJobPreparationGate();
        var expected = Key();
        var observed = new DadRequestedJobPreparationKey(
            runId,
            new DadWorkerSessionId(workerSessionId),
            slotId,
            new DadAccountKey(accountKey),
            new DadCharacterKey(characterKey),
            contentId,
            requiredJobId);
        var calls = 0;

        var proof = gate.Advance(expected, Observation(observed), Start, _ =>
        {
            calls++;
            return DadClassJobEquipAttemptResult.Success();
        });

        Assert.Equal(DadRequestedJobPreparationStatus.Pending, proof.Status);
        Assert.Equal(0, proof.AttemptCount);
        Assert.Equal(0, calls);
        Assert.False(DadRequestedJobPreparationProofRules.PermitsReadiness(proof, expected));
    }

    [Fact]
    public void LoadingOrWrongCharacterDoesNotResetConsumedActualCalls()
    {
        var gate = new DadRequestedJobPreparationGate();
        var expected = Key();
        var wrongCharacter = expected with
        {
            CharacterKey = new DadCharacterKey("Other Character@Excalibur"),
            ContentId = 4321,
        };

        var first = gate.Advance(
            expected,
            Observation(expected),
            Start,
            _ => DadClassJobEquipAttemptResult.Rejected("first actual call rejected"));
        var transition = gate.Advance(
            expected,
            Observation(wrongCharacter),
            Start.AddSeconds(10),
            _ => throw new InvalidOperationException("wrong character must not equip"));
        var second = gate.Advance(
            expected,
            Observation(expected),
            Start.AddSeconds(11),
            _ => DadClassJobEquipAttemptResult.Rejected("second actual call rejected"));

        Assert.Equal(1, first.AttemptCount);
        Assert.Equal(1, transition.AttemptCount);
        Assert.Equal(DadRequestedJobPreparationStatus.Pending, transition.Status);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public void CompletedProofBecomesSoftFailedIfCurrentJobDrifts()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();

        gate.Advance(key, Observation(key, currentJobId: 21), Start, null);
        var drifted = gate.Advance(key, Observation(key, currentJobId: 19), Start.AddSeconds(1), null);

        Assert.Equal(DadRequestedJobPreparationStatus.SoftFailed, drifted.Status);
        Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(drifted, key));
        Assert.True(DadRequestedJobPreparationProofRules.PermitsReadiness(drifted, key, currentJobId: 19));
    }

    [Fact]
    public void ResetAllowsSameExactKeyToPrepareAgain()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var calls = 0;

        gate.Advance(key, Observation(key), Start, _ =>
        {
            calls++;
            return DadClassJobEquipAttemptResult.Success();
        });
        gate.Reset();
        var second = gate.Advance(key, Observation(key), Start.AddSeconds(10), _ =>
        {
            calls++;
            return DadClassJobEquipAttemptResult.Success();
        });

        Assert.Equal(2, calls);
        Assert.Equal(1, second.AttemptCount);
        Assert.Equal(DadRequestedJobPreparationStatus.AwaitingVerification, second.Status);
    }

    [Fact]
    public void ProofCloneCannotCorruptStoredState()
    {
        var gate = new DadRequestedJobPreparationGate();
        var key = Key();
        var first = gate.Advance(key, Observation(key), Start, _ => DadClassJobEquipAttemptResult.Success());
        first.Status = DadRequestedJobPreparationStatus.SoftFailed;
        first.AttemptCount = 99;

        Assert.True(gate.TryGet(key, out var stored));
        Assert.Equal(DadRequestedJobPreparationStatus.AwaitingVerification, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    private static DadRequestedJobPreparationKey Key(uint? requiredJobId = 21)
        => new(
            "run-1",
            new DadWorkerSessionId("worker-1"),
            "Slot1",
            new DadAccountKey("account-1"),
            new DadCharacterKey("Player One@Excalibur"),
            1234,
            requiredJobId);

    private static DadRequestedJobPreparationObservation Observation(
        DadRequestedJobPreparationKey identity,
        uint currentJobId = 19,
        bool isSafeToEquip = true,
        DadClassJobGearsetCatalogSnapshot? catalog = null)
        => new(
            identity,
            currentJobId,
            isSafeToEquip,
            catalog ?? Catalog(),
            isSafeToEquip ? string.Empty : "unsafe state");

    private static DadClassJobGearsetCatalogSnapshot Catalog()
        => DadClassJobGearsetCatalogSnapshot.Success(
            [
                new DadClassJobGearsetSnapshot(11, 21, Exists: true, IsValid: true),
                new DadClassJobGearsetSnapshot(4, 21, Exists: true, IsValid: true),
            ]);
}
