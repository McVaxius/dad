namespace dad.Services;

public enum DadPartyTeardownAction
{
    None = 0,
    SendBreakup = 1,
    ApprovePrompt = 2,
    Complete = 3,
    Fail = 4,
    InvokePartyMenuLeave = 5,
}

public sealed record DadPartyTeardownObservation(
    DateTime NowUtc,
    ulong LocalContentId,
    ulong PartyLeaderContentId,
    IReadOnlyCollection<ulong> PartyMemberContentIds,
    bool IsCrossRealmParty,
    bool IsInDuty,
    bool IsQueued,
    bool IsWorldStable,
    bool PartyMenuVisible,
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
    public const string BreakupCommand = "/partycmd breakup";
    public const int PartyMenuLeaveCallbackOperation = 2;
    public const int PartyMenuLeaveCallbackArgument = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AttemptThrottle = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SoloConfirmationInterval = TimeSpan.FromSeconds(1);
    public const int MaximumAttempts = 3;

    private readonly HashSet<ulong> expectedMembers;
    private readonly ulong expectedLeaderContentId;
    private readonly DateTime startedAtUtc;
    private DateTime nextAttemptUtc;
    private int commandAttempts;
    private bool lastPromptVisible;
    private bool commandSent;
    private bool approvalSent;
    private int partyMenuCallbackAttempts;
    private DateTime? soloConfirmedSinceUtc;

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
        nextAttemptUtc = startedAtUtc;
    }

    public int CommandAttempts => commandAttempts;

    public DadPartyTeardownDecision Pulse(DadPartyTeardownObservation observation)
    {
        var actualMembers = observation.PartyMemberContentIds.Where(static id => id != 0).ToHashSet();
        var partyStateConfirmsSolo = !observation.IsCrossRealmParty &&
                                     observation.LocalContentId != 0 &&
                                     (actualMembers.Count == 0 ||
                                      (actualMembers.Count == 1 && actualMembers.Contains(observation.LocalContentId)));
        if (partyStateConfirmsSolo && expectedMembers.Count <= 1)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Complete, "Party teardown complete; the local character is already solo.");

        if (observation.NowUtc - startedAtUtc >= Timeout)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, $"Party teardown timed out after {commandAttempts} breakup command attempt(s).");

        var promptJustAppeared = observation.PromptVisible && !lastPromptVisible;
        lastPromptVisible = observation.PromptVisible;

        if (observation.IsInDuty || observation.IsQueued)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for out-of-duty, not-queued teardown state.");

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
        {
            if (!partyStateConfirmsSolo)
            {
                soloConfirmedSinceUtc = null;
                return new DadPartyTeardownDecision(
                    DadPartyTeardownAction.None,
                    observation.IsCrossRealmParty
                        ? "Waiting for InfoProxyCrossRealm to confirm the cross-world party has disbanded."
                        : "Waiting for PartyList to confirm the party has disbanded.");
            }

            soloConfirmedSinceUtc ??= observation.NowUtc;
            if (observation.NowUtc - soloConfirmedSinceUtc.Value < SoloConfirmationInterval)
            {
                return new DadPartyTeardownDecision(
                    DadPartyTeardownAction.None,
                    "Party absence observed; waiting for sustained cross-world-safe solo confirmation.");
            }

            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Complete,
                "Party teardown complete; cross-world party state is inactive and PartyList remained solo after prompt approval.");
        }

        if (observation.IsCrossRealmParty &&
            observation.PartyMenuVisible &&
            commandSent &&
            partyMenuCallbackAttempts < commandAttempts)
        {
            partyMenuCallbackAttempts++;
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.InvokePartyMenuLeave,
                $"Invoking PartyMemberList callback {PartyMenuLeaveCallbackOperation}, {PartyMenuLeaveCallbackArgument} for cross-world leave attempt {partyMenuCallbackAttempts}/{MaximumAttempts}.");
        }

        var unexpectedMembers = actualMembers.Where(member => !expectedMembers.Contains(member)).ToArray();
        if (!partyStateConfirmsSolo && unexpectedMembers.Length > 0)
        {
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Fail,
                $"Party membership contains unexpected Content ID(s) {string.Join(",", unexpectedMembers)}; refusing teardown mutation.");
        }

        if (observation.LocalContentId == 0 ||
            expectedLeaderContentId == 0 ||
            observation.LocalContentId != expectedLeaderContentId ||
            (!partyStateConfirmsSolo && observation.PartyLeaderContentId != expectedLeaderContentId))
        {
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Fail, "The local frozen leader is no longer proven by the authoritative party source; refusing teardown mutation.");
        }

        if (observation.NowUtc < nextAttemptUtc || commandAttempts >= MaximumAttempts)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for the guarded breakup attempt or confirmation window.");

        commandAttempts++;
        commandSent = true;
        nextAttemptUtc = observation.NowUtc + AttemptThrottle;
        return new DadPartyTeardownDecision(DadPartyTeardownAction.SendBreakup, $"Sending guarded party breakup command attempt {commandAttempts}/{MaximumAttempts}.");
    }
}
