using dad.Models;

namespace dad.Services;

public enum DadHomeWorldReturnAction
{
    Ready = 0,
    Wait = 1,
    InvokeLifestream = 2,
    Reject = 3,
}

public readonly record struct DadHomeWorldReturnDecision(
    DadHomeWorldReturnAction Action,
    string Summary,
    string DestinationWorldName = "",
    int AttemptNumber = 0);

public sealed class DadHomeWorldReturnGate
{
    private string immutableIdentity = string.Empty;
    private bool invocationPending;
    private bool acceptedInvocation;
    private bool terminalFailure;
    private int invocationCount;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private string terminalSummary = string.Empty;

    public int InvocationCount => invocationCount;
    public bool AcceptedInvocation => acceptedInvocation;
    public bool InvocationPending => invocationPending;

    public DadHomeWorldReturnDecision Evaluate(
        DadParticipantSnapshot participant,
        bool lifestreamAvailable,
        bool lifestreamBusy,
        DateTime nowUtc)
    {
        participant ??= new DadParticipantSnapshot();
        if (terminalFailure)
            return Reject(terminalSummary);

        if (acceptedInvocation &&
            (!participant.IsAvailable ||
             !participant.WorldReadyStable ||
             !DadCoordinatorTravelRules.IsFreshComplete(participant.CurrentLocation, nowUtc)))
        {
            return Wait("Lifestream accepted return-home travel; waiting for fresh world-stable home proof.");
        }

        var character = participant.Character ?? new DadAcquiredCharacter();
        if (participant.ActiveCharacterKey.IsEmpty ||
            string.IsNullOrWhiteSpace(character.CharacterKey) ||
            character.ContentId == 0 ||
            character.WorldId == 0 ||
            string.IsNullOrWhiteSpace(character.WorldName) ||
            !Same(participant.ActiveCharacterKey.Value, character.CharacterKey))
        {
            return Fail("Return-home relog safety requires exact current character, Content ID, and recorded home-world identity.");
        }

        var identity = $"{participant.ActiveCharacterKey.Value.Trim()}|{character.ContentId}|{character.WorldId}|{character.WorldName.Trim()}";
        if (string.IsNullOrWhiteSpace(immutableIdentity))
            immutableIdentity = identity;
        else if (!string.Equals(immutableIdentity, identity, StringComparison.OrdinalIgnoreCase))
            return Fail("Current character or recorded home-world identity changed during return-home preparation.");

        if (!participant.IsAvailable || !participant.WorldReadyStable)
            return Wait("Waiting for fresh world-stable current-character proof before return-home travel.");
        var current = participant.CurrentLocation;
        if (current is not { IsComplete: true })
            return Fail("Return-home relog safety requires exact current-world, data-center, and region identity.");
        if (!DadCoordinatorTravelRules.IsFreshComplete(current, nowUtc))
            return Wait("Waiting for fresh current-world proof before return-home travel.");

        var atHome = current.WorldId == character.WorldId && Same(current.WorldName, character.WorldName);
        if (atHome)
        {
            return lifestreamAvailable && !lifestreamBusy
                ? Ready($"Current character is world-stable on home world {character.WorldName}, and Lifestream is idle.")
                : Wait($"Home world {character.WorldName} is proven; waiting for idle readable Lifestream before relog.");
        }

        if (acceptedInvocation)
            return Wait($"Lifestream accepted travel to home world {character.WorldName}; waiting for fresh home-world proof.");
        if (!lifestreamAvailable || lifestreamBusy)
            return Wait("Waiting for Lifestream to be available and idle before return-home travel.");
        if (invocationPending)
            return Wait("Lifestream.ChangeWorld result is pending; no duplicate invocation is permitted.");

        var now = EnsureUtc(nowUtc);
        if (now < nextAttemptUtc)
            return Wait($"Waiting until {nextAttemptUtc:O} before retrying an explicit-false return-home result.");
        if (invocationCount >= DadClientTravelGate.MaxChangeWorldAttempts)
            return Fail("Return-home Lifestream.ChangeWorld exhausted three explicit-false attempts.");

        invocationPending = true;
        return new DadHomeWorldReturnDecision(
            DadHomeWorldReturnAction.InvokeLifestream,
            $"Invoke Lifestream.ChangeWorld('{character.WorldName.Trim()}') attempt {invocationCount + 1}/{DadClientTravelGate.MaxChangeWorldAttempts} before relog.",
            character.WorldName.Trim(),
            invocationCount + 1);
    }

    public void RecordInvocationResult(DadLifestreamChangeWorldResult result, DateTime nowUtc)
    {
        if (!invocationPending || acceptedInvocation || terminalFailure)
            return;

        invocationPending = false;
        invocationCount++;
        switch (result.Outcome)
        {
            case DadLifestreamChangeWorldOutcome.Accepted:
                acceptedInvocation = true;
                break;
            case DadLifestreamChangeWorldOutcome.ExplicitFalse
                when invocationCount < DadClientTravelGate.MaxChangeWorldAttempts:
                nextAttemptUtc = EnsureUtc(nowUtc) + DadClientTravelGate.RetryInterval;
                break;
            case DadLifestreamChangeWorldOutcome.ExplicitFalse:
                terminalFailure = true;
                terminalSummary = "Return-home Lifestream.ChangeWorld exhausted three explicit-false attempts.";
                break;
            default:
                terminalFailure = true;
                terminalSummary = string.IsNullOrWhiteSpace(result.Summary)
                    ? "Return-home Lifestream.ChangeWorld acceptance is uncertain; no retry is permitted."
                    : $"Return-home Lifestream.ChangeWorld acceptance is uncertain; no retry is permitted. {result.Summary}";
                break;
        }
    }

    private DadHomeWorldReturnDecision Fail(string summary)
    {
        terminalFailure = true;
        terminalSummary = summary;
        return Reject(summary);
    }

    private static DadHomeWorldReturnDecision Ready(string summary)
        => new(DadHomeWorldReturnAction.Ready, summary);

    private static DadHomeWorldReturnDecision Wait(string summary)
        => new(DadHomeWorldReturnAction.Wait, summary);

    private static DadHomeWorldReturnDecision Reject(string summary)
        => new(DadHomeWorldReturnAction.Reject, summary);

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
