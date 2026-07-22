namespace dad.Services;

public enum DadParticipantPartyDepartureAction
{
    None = 0,
    SendLeave = 1,
    InvokePartyMenuLeave = 2,
    ApprovePrompt = 3,
    Complete = 4,
    Fail = 5,
}

public sealed record DadParticipantPartyDepartureObservation(
    DateTime NowUtc,
    ulong LocalContentId,
    ulong ExpectedInviterContentId,
    IReadOnlyCollection<ulong> PartyMemberContentIds,
    bool IsCrossRealmParty,
    bool IsInDuty,
    bool IsQueued,
    bool IsWorldStable,
    bool PartyMenuVisible,
    bool PromptVisible,
    string PromptIdentity,
    string PromptText);

public sealed record DadParticipantPartyDepartureDecision(
    DadParticipantPartyDepartureAction Action,
    string Summary);

/// <summary>
/// Runtime-independent safety controller used only before participant invite acceptance. Successful-run
/// party teardown has its own controller and is intentionally not shared with this path.
/// </summary>
public sealed class DadParticipantPartyDepartureController
{
    public const string LeaveCommand = "/partycmd leave";
    public const int PartyMenuLeaveCallbackOperation = 2;
    public const int PartyMenuLeaveCallbackArgument = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AttemptThrottle = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan SoloConfirmationInterval = TimeSpan.FromSeconds(1);
    public const int MaximumAttempts = 7;

    private readonly ulong expectedInviterContentId;
    private readonly DateTime startedAtUtc;
    private DateTime nextAttemptUtc;
    private int commandAttempts;
    private bool lastPromptVisible;
    private bool commandSent;
    private int approvedCommandAttempt;
    private int partyMenuCallbackAttempts;
    private DateTime? soloConfirmedSinceUtc;

    public DadParticipantPartyDepartureController(
        ulong expectedInviterContentId,
        DateTime startedAtUtc,
        bool promptVisible)
    {
        this.expectedInviterContentId = expectedInviterContentId;
        this.startedAtUtc = startedAtUtc;
        lastPromptVisible = promptVisible;
        nextAttemptUtc = startedAtUtc;
    }

    public int CommandAttempts => commandAttempts;

    public DadParticipantPartyDepartureDecision Pulse(DadParticipantPartyDepartureObservation observation)
    {
        var actualMembers = observation.PartyMemberContentIds.Where(static id => id != 0).ToHashSet();
        if (expectedInviterContentId == 0 || observation.ExpectedInviterContentId != expectedInviterContentId)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Fail,
                "The frozen expected inviter identity changed during participant party recovery.");
        }

        if (actualMembers.Contains(expectedInviterContentId))
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Complete,
                "The authoritative party source already contains the exact expected inviter; no departure is needed.");
        }

        var partyStateConfirmsSolo = !observation.IsCrossRealmParty &&
                                     observation.LocalContentId != 0 &&
                                     (actualMembers.Count == 0 ||
                                      actualMembers.Count == 1 && actualMembers.Contains(observation.LocalContentId));
        if (partyStateConfirmsSolo && !commandSent)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Complete,
                "The participant is already solo; no departure mutation is needed.");
        }

        if (observation.NowUtc - startedAtUtc >= Timeout)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Fail,
                $"Participant party departure timed out after {commandAttempts} leave command attempt(s).");
        }

        var promptJustAppeared = observation.PromptVisible && !lastPromptVisible;
        lastPromptVisible = observation.PromptVisible;

        if (observation.IsInDuty || observation.IsQueued || !observation.IsWorldStable)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.None,
                "Waiting for a stable out-of-duty, not-queued state before participant party departure.");
        }

        if (observation.LocalContentId == 0)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Fail,
                "The local participant Content ID is unavailable; refusing party departure mutation.");
        }

        if (observation.PromptVisible)
        {
            if (!commandSent || !promptJustAppeared || approvedCommandAttempt == commandAttempts)
            {
                return new DadParticipantPartyDepartureDecision(
                    DadParticipantPartyDepartureAction.None,
                    "A pre-existing or already-handled confirmation is visible; it will not be approved.");
            }

            approvedCommandAttempt = commandAttempts;
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.ApprovePrompt,
                $"Approving the fresh participant leave confirmation for attempt {commandAttempts}/{MaximumAttempts}.");
        }

        if (commandSent && partyStateConfirmsSolo)
        {
            soloConfirmedSinceUtc ??= observation.NowUtc;
            if (observation.NowUtc - soloConfirmedSinceUtc.Value < SoloConfirmationInterval)
            {
                return new DadParticipantPartyDepartureDecision(
                    DadParticipantPartyDepartureAction.None,
                    "Party absence observed; waiting for sustained authoritative solo confirmation.");
            }

            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.Complete,
                "Participant party departure is complete; authoritative state remained solo after the leave mutation.");
        }

        soloConfirmedSinceUtc = null;

        if (observation.IsCrossRealmParty &&
            observation.PartyMenuVisible &&
            commandSent &&
            partyMenuCallbackAttempts < commandAttempts)
        {
            partyMenuCallbackAttempts++;
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.InvokePartyMenuLeave,
                $"Invoking PartyMemberList callback {PartyMenuLeaveCallbackOperation}, {PartyMenuLeaveCallbackArgument} for participant leave attempt {partyMenuCallbackAttempts}/{MaximumAttempts}.");
        }

        if (observation.NowUtc < nextAttemptUtc || commandAttempts >= MaximumAttempts)
        {
            return new DadParticipantPartyDepartureDecision(
                DadParticipantPartyDepartureAction.None,
                "Waiting for the guarded participant leave attempt or confirmation window.");
        }

        commandAttempts++;
        commandSent = true;
        nextAttemptUtc = observation.NowUtc + AttemptThrottle;
        return new DadParticipantPartyDepartureDecision(
            DadParticipantPartyDepartureAction.SendLeave,
            $"Sending guarded participant party leave command attempt {commandAttempts}/{MaximumAttempts}.");
    }
}
