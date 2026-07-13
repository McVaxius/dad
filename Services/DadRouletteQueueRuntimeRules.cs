namespace dad.Services;

public static class DadRouletteSelectionProof
{
    public static bool IsExact(
        bool hasRouletteSelected,
        bool selectedContentIsRoulette,
        uint selectedId,
        uint requestedRouletteId)
        => requestedRouletteId is > 0 and <= byte.MaxValue &&
           hasRouletteSelected &&
           selectedContentIsRoulette &&
           selectedId == requestedRouletteId;
}

public enum DadRouletteQueueMutation
{
    Wait,
    ClearSelection,
    OpenRoulette,
    Join,
    Fail,
}

public readonly record struct DadRouletteQueueDecision(
    DadRouletteQueueMutation Mutation,
    string Reason = "");

public sealed class DadRouletteQueueAttemptGate
{
    public const int MaxSelectionAttempts = 3;
    public const int MaxJoinAttempts = 3;
    public static readonly TimeSpan SelectionSettle = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan RegistrationGrace = TimeSpan.FromSeconds(8);

    private bool clearPending = true;
    private bool openPending;
    private bool awaitingSelectionProof;
    private DateTime selectionProofAtUtc = DateTime.MinValue;
    private DateTime registrationGraceUntilUtc = DateTime.MinValue;

    public int SelectionAttempts { get; private set; }
    public int JoinAttempts { get; private set; }

    public bool IsRegistrationGraceActive(DateTime nowUtc)
        => registrationGraceUntilUtc != DateTime.MinValue && nowUtc < registrationGraceUntilUtc;

    public DadRouletteQueueDecision Decide(
        DateTime nowUtc,
        bool exactRouletteSelected,
        bool registrationEvidenceObserved)
    {
        if (registrationEvidenceObserved)
            return new DadRouletteQueueDecision(DadRouletteQueueMutation.Wait, "Queue/commence/transition evidence observed.");

        if (registrationGraceUntilUtc != DateTime.MinValue)
        {
            if (nowUtc < registrationGraceUntilUtc)
                return new DadRouletteQueueDecision(DadRouletteQueueMutation.Wait, "Waiting for Duty Finder registration evidence.");

            registrationGraceUntilUtc = DateTime.MinValue;
            if (JoinAttempts >= MaxJoinAttempts)
                return new DadRouletteQueueDecision(DadRouletteQueueMutation.Fail, "Daily Roulette registration did not appear after three Join attempts.");

            if (!exactRouletteSelected)
                clearPending = true;
        }

        if (awaitingSelectionProof)
        {
            if (nowUtc < selectionProofAtUtc)
                return new DadRouletteQueueDecision(DadRouletteQueueMutation.Wait, "Waiting six seconds for roulette selection to settle.");

            awaitingSelectionProof = false;
            if (!exactRouletteSelected)
            {
                clearPending = true;
                if (SelectionAttempts >= MaxSelectionAttempts)
                    return new DadRouletteQueueDecision(DadRouletteQueueMutation.Fail, "Daily Roulette selection could not be proven after three attempts.");
            }
        }

        if (openPending)
        {
            openPending = false;
            SelectionAttempts++;
            awaitingSelectionProof = true;
            selectionProofAtUtc = nowUtc + SelectionSettle;
            return new DadRouletteQueueDecision(DadRouletteQueueMutation.OpenRoulette);
        }

        if (exactRouletteSelected && !clearPending)
        {
            if (JoinAttempts >= MaxJoinAttempts)
                return new DadRouletteQueueDecision(DadRouletteQueueMutation.Fail, "Daily Roulette registration did not appear after three Join attempts.");

            JoinAttempts++;
            registrationGraceUntilUtc = nowUtc + RegistrationGrace;
            return new DadRouletteQueueDecision(DadRouletteQueueMutation.Join);
        }

        if (clearPending)
        {
            if (SelectionAttempts >= MaxSelectionAttempts)
                return new DadRouletteQueueDecision(DadRouletteQueueMutation.Fail, "Daily Roulette selection could not be proven after three attempts.");

            clearPending = false;
            openPending = true;
            return new DadRouletteQueueDecision(DadRouletteQueueMutation.ClearSelection);
        }

        return new DadRouletteQueueDecision(DadRouletteQueueMutation.Wait, "Waiting for roulette selection state.");
    }

    public void Reset()
    {
        clearPending = true;
        openPending = false;
        awaitingSelectionProof = false;
        selectionProofAtUtc = DateTime.MinValue;
        registrationGraceUntilUtc = DateTime.MinValue;
        SelectionAttempts = 0;
        JoinAttempts = 0;
    }
}

public sealed class DadRouletteTerritoryEvidenceGate
{
    public bool EntryEvidenceObserved { get; private set; }
    public uint CapturedTerritoryId { get; private set; }

    public void ObserveEntryEvidence()
        => EntryEvidenceObserved = true;

    public bool TryCapture(bool boundByDuty, uint territoryId)
    {
        if (!EntryEvidenceObserved || !boundByDuty || territoryId == 0)
            return false;

        if (CapturedTerritoryId == 0)
            CapturedTerritoryId = territoryId;

        return CapturedTerritoryId == territoryId;
    }

    public bool IsInCapturedDuty(bool boundByDuty, uint territoryId)
        => CapturedTerritoryId != 0 && boundByDuty && territoryId == CapturedTerritoryId;

    public bool MatchesCompletion(uint territoryId, DateTime completedAtUtc, DateTime runStartedAtUtc)
        => CapturedTerritoryId != 0 &&
           territoryId == CapturedTerritoryId &&
           completedAtUtc >= runStartedAtUtc;

    public void Reset()
    {
        EntryEvidenceObserved = false;
        CapturedTerritoryId = 0;
    }
}

public sealed class DadUnrestrictedPartyOverrideLease
{
    public bool IsActive { get; private set; }
    public bool PreviousValue { get; private set; }

    public bool Ensure(
        bool requiredValue,
        Func<bool> read,
        Action<bool> write,
        out bool changed,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        changed = false;
        failure = string.Empty;
        try
        {
            var current = read();
            if (!IsActive)
            {
                PreviousValue = current;
                IsActive = true;
            }

            if (current != requiredValue)
            {
                write(requiredValue);
                changed = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    public bool Restore(Func<bool> read, Action<bool> write, out string failure)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        failure = string.Empty;
        if (!IsActive)
            return true;

        try
        {
            if (read() != PreviousValue)
                write(PreviousValue);
            IsActive = false;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }
}
