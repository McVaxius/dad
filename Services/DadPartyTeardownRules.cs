namespace dad.Services;

public enum DadPartyTeardownAction
{
    None = 0,
    SendBreakup = 1,
    ApprovePrompt = 2,
    Complete = 3,
    Fail = 4,
}

public sealed record DadPartyTeardownObservation(
    DateTime NowUtc,
    ulong LocalContentId,
    ulong PartyLeaderContentId,
    IReadOnlyCollection<ulong> PartyMemberContentIds,
    bool IsInDuty,
    bool IsQueued,
    bool IsWorldStable,
    bool PromptVisible,
    string PromptIdentity,
    string PromptText,
    string InviterName);

public sealed record DadPartyTeardownDecision(DadPartyTeardownAction Action, string Summary);

/// <summary>
/// Stateful, runtime-independent safety gate for successful-run party teardown.
/// </summary>
public sealed class DadPartyTeardownController
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AttemptThrottle = TimeSpan.FromSeconds(10);
    public const int MaximumAttempts = 3;

    private readonly HashSet<ulong> expectedMembers;
    private readonly ulong expectedLeaderContentId;
    private readonly DateTime startedAtUtc;
    private DateTime nextAttemptUtc;
    private int commandAttempts;
    private bool lastPromptVisible;
    private bool commandSent;
    private bool approvalSent;
    private bool frozenRosterObserved;

    public DadPartyTeardownController(
        IEnumerable<ulong> expectedMemberContentIds,
        ulong expectedLeaderContentId,
        DateTime startedAtUtc,
        bool promptVisible,
        string promptIdentity)
    {
        expectedMembers = expectedMemberContentIds.Where(static id => id != 0).ToHashSet();
        this.expectedLeaderContentId = expectedLeaderContentId;
        this.startedAtUtc = startedAtUtc;
        lastPromptVisible = promptVisible;
        frozenRosterObserved = expectedMembers.Count <= 1;
        nextAttemptUtc = startedAtUtc;
    }

    public int CommandAttempts => commandAttempts;

    public DadPartyTeardownDecision Pulse(DadPartyTeardownObservation observation)
    {
        var actualMembers = observation.PartyMemberContentIds.Where(static id => id != 0).ToHashSet();
        if (actualMembers.SetEquals(expectedMembers))
            frozenRosterObserved = true;

        var partyListConfirmsSolo = observation.LocalContentId != 0 &&
                                    (actualMembers.Count == 0 ||
                                     (actualMembers.Count == 1 && actualMembers.Contains(observation.LocalContentId)));
        if (partyListConfirmsSolo && frozenRosterObserved)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Complete, "Party teardown complete; the local character is already solo.");

        if (observation.NowUtc - startedAtUtc >= Timeout)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, $"Party teardown timed out after {commandAttempts} breakup command attempt(s).");

        if (partyListConfirmsSolo)
        {
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.None,
                "PartyList temporarily reported solo before confirming the exact frozen party; waiting for membership truth.");
        }

        if (!actualMembers.SetEquals(expectedMembers))
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, "Party membership no longer matches the exact frozen roster; refusing teardown mutation.");

        if (observation.LocalContentId == 0 ||
            expectedLeaderContentId == 0 ||
            observation.LocalContentId != expectedLeaderContentId ||
            observation.PartyLeaderContentId != expectedLeaderContentId)
        {
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, "The local frozen leader is no longer proven by PartyLeaderIndex; refusing teardown mutation.");
        }

        if (observation.IsInDuty || observation.IsQueued || !observation.IsWorldStable)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for out-of-duty, not-queued, world-stable teardown state.");

        var promptJustAppeared = observation.PromptVisible && !lastPromptVisible;
        lastPromptVisible = observation.PromptVisible;

        if (observation.PromptVisible)
        {
            if (!commandSent || !promptJustAppeared || approvalSent)
            {
                return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "A pre-existing or already-handled confirmation is visible; it will not be approved.");
            }

            approvalSent = true;
            return new DadPartyTeardownDecision(DadPartyTeardownAction.ApprovePrompt, "Approving the newly appeared breakup confirmation associated with this command.");
        }

        if (approvalSent)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for PartyList to prove the party has disbanded.");

        if (observation.NowUtc < nextAttemptUtc || commandAttempts >= MaximumAttempts)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for the guarded breakup attempt or confirmation window.");

        commandAttempts++;
        commandSent = true;
        nextAttemptUtc = observation.NowUtc + AttemptThrottle;
        return new DadPartyTeardownDecision(DadPartyTeardownAction.SendBreakup, $"Sending guarded party breakup command attempt {commandAttempts}/{MaximumAttempts}.");
    }
}
