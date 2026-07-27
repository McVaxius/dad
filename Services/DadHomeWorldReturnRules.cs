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
    private string frozenIdentity = string.Empty;
    private string frozenSourceCharacterKey = string.Empty;
    private string frozenHomeWorldName = string.Empty;
    private string frozenRelogTargetCharacterKey = string.Empty;
    private bool invocationPending;
    private bool acceptedInvocation;
    private bool terminalFailure;
    private int invocationCount;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private string terminalSummary = string.Empty;

    public int InvocationCount => invocationCount;
    public bool AcceptedInvocation => acceptedInvocation;
    public bool InvocationPending => invocationPending;
    internal string FrozenSourceCharacterKey => frozenSourceCharacterKey;
    internal string FrozenHomeWorldName => frozenHomeWorldName;
    internal string FrozenRelogTargetCharacterKey => frozenRelogTargetCharacterKey;

    public DadHomeWorldReturnDecision Evaluate(
        DadParticipantSnapshot participant,
        bool lifestreamAvailable,
        bool lifestreamBusy,
        DadCharacterKey relogTarget,
        DateTime nowUtc)
    {
        participant ??= new DadParticipantSnapshot();
        if (terminalFailure)
            return Reject(terminalSummary);
        if (string.IsNullOrWhiteSpace(frozenIdentity))
        {
            var freezeError = TryFreezeIdentity(participant, relogTarget);
            if (!string.IsNullOrWhiteSpace(freezeError))
                return Fail(freezeError);
        }
        else if (!Same(frozenRelogTargetCharacterKey, relogTarget.Value))
        {
            return Fail("The frozen relog target changed during return-home preparation.");
        }

        if (!participant.IsAvailable ||
            !participant.WorldReadyStable ||
            !DadCoordinatorTravelRules.IsFreshComplete(participant.CurrentLocation, nowUtc))
        {
            return Wait(acceptedInvocation
                ? BuildAcceptedTravelSummary()
                : BuildBeforeAcceptanceSummary("waiting for fresh source-world proof."));
        }

        var identityError = ValidateFrozenIdentity(participant);
        if (!string.IsNullOrWhiteSpace(identityError))
            return Fail(identityError);

        var current = participant.CurrentLocation!;
        var atHome = current.WorldId == participant.Character.WorldId &&
                     Same(current.WorldName, frozenHomeWorldName);
        if (atHome)
        {
            return lifestreamAvailable && !lifestreamBusy
                ? Ready(
                    $"{frozenSourceCharacterKey} is world-stable on home world {frozenHomeWorldName}; " +
                    $"Lifestream is idle before DAD relogs to {frozenRelogTargetCharacterKey}.")
                : Wait(
                    $"{frozenSourceCharacterKey} has fresh home-world proof for {frozenHomeWorldName} before DAD relogs to " +
                    $"{frozenRelogTargetCharacterKey}; waiting for readable idle Lifestream.");
        }

        if (acceptedInvocation)
            return Wait(BuildAcceptedTravelSummary());
        if (!lifestreamAvailable || lifestreamBusy)
            return Wait(BuildBeforeAcceptanceSummary("waiting for readable idle Lifestream."));
        if (invocationPending)
            return Wait(BuildBeforeAcceptanceSummary("waiting for the pending ChangeWorld result without a duplicate."));

        var now = EnsureUtc(nowUtc);
        if (now < nextAttemptUtc)
        {
            return Wait(BuildBeforeAcceptanceSummary(
                $"waiting until {nextAttemptUtc:O} after an explicit-false ChangeWorld result."));
        }
        if (invocationCount >= DadClientTravelGate.MaxChangeWorldAttempts)
            return Fail("Return-home Lifestream.ChangeWorld exhausted three explicit-false attempts.");

        invocationPending = true;
        return new DadHomeWorldReturnDecision(
            DadHomeWorldReturnAction.InvokeLifestream,
            BuildBeforeAcceptanceSummary(
                $"invoking Lifestream.ChangeWorld('{frozenHomeWorldName}') attempt {invocationCount + 1}/{DadClientTravelGate.MaxChangeWorldAttempts}."),
            frozenHomeWorldName,
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

    private string TryFreezeIdentity(
        DadParticipantSnapshot participant,
        DadCharacterKey relogTarget)
    {
        var character = participant.Character ?? new DadAcquiredCharacter();
        if (participant.ActiveCharacterKey.IsEmpty ||
            string.IsNullOrWhiteSpace(character.CharacterKey) ||
            character.ContentId == 0 ||
            character.WorldId == 0 ||
            string.IsNullOrWhiteSpace(character.WorldName) ||
            !Same(participant.ActiveCharacterKey.Value, character.CharacterKey))
        {
            return "Return-home relog safety requires exact current character, Content ID, and recorded home-world identity.";
        }
        if (!DadWakePolicyRules.IsValidCharacterKey(relogTarget))
            return "Return-home relog safety requires an exact frozen Name@World relog target.";

        frozenSourceCharacterKey = participant.ActiveCharacterKey.Value.Trim();
        frozenHomeWorldName = character.WorldName.Trim();
        frozenRelogTargetCharacterKey = relogTarget.Value.Trim();
        frozenIdentity =
            $"{frozenSourceCharacterKey}|{character.ContentId}|{character.WorldId}|{frozenHomeWorldName}";
        return string.Empty;
    }

    private string ValidateFrozenIdentity(DadParticipantSnapshot participant)
    {
        var character = participant.Character ?? new DadAcquiredCharacter();
        if (participant.ActiveCharacterKey.IsEmpty ||
            string.IsNullOrWhiteSpace(character.CharacterKey) ||
            character.ContentId == 0 ||
            character.WorldId == 0 ||
            string.IsNullOrWhiteSpace(character.WorldName) ||
            !Same(participant.ActiveCharacterKey.Value, character.CharacterKey))
        {
            return "Return-home relog safety requires exact current character, Content ID, and recorded home-world identity.";
        }

        var identity =
            $"{participant.ActiveCharacterKey.Value.Trim()}|{character.ContentId}|{character.WorldId}|{character.WorldName.Trim()}";
        if (!string.Equals(frozenIdentity, identity, StringComparison.OrdinalIgnoreCase))
            return "Current character or recorded home-world identity changed during return-home preparation.";
        if (participant.CurrentLocation is not { IsComplete: true })
            return "Return-home relog safety requires exact current-world, data-center, and region identity.";
        return string.Empty;
    }

    private string BuildBeforeAcceptanceSummary(string suffix)
        => $"{frozenSourceCharacterKey} is waiting to start Data Center travel back to home world " +
           $"{frozenHomeWorldName} before DAD relogs to {frozenRelogTargetCharacterKey}; {suffix}";

    private string BuildAcceptedTravelSummary()
        => $"{frozenSourceCharacterKey} is Data Center traveling back to home world {frozenHomeWorldName} " +
           $"before DAD relogs to {frozenRelogTargetCharacterKey}; waiting for fresh home-world proof.";

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
