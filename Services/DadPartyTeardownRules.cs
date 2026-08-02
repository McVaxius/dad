namespace dad.Services;

public enum DadPartyTeardownMutationMode
{
    DisbandAsLeader = 0,
    LeaveAsFollower = 1,
}

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
    string InviterName,
    bool PromptReady = true,
    bool OtherReadyPromptVisible = false);

public sealed record DadPartyTeardownDecision(
    DadPartyTeardownAction Action,
    string Summary,
    bool PromptOverrideUsed = false,
    string PromptAudit = "");

/// <summary>
/// Stateful, runtime-independent safety gate for successful-run party teardown.
/// </summary>
public sealed class DadPartyTeardownController
{
    public const string BreakupCommand = "/partycmd breakup";
    public const string LeaveCommand = "/partycmd leave";
    public const int PartyMenuLeaveCallbackOperation = 2;
    public const int PartyMenuLeaveCallbackArgument = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AttemptThrottle = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan SoloConfirmationInterval = TimeSpan.FromSeconds(1);
    public const int MaximumAttempts = 7;

    private readonly HashSet<ulong> expectedMembers;
    private readonly ulong expectedLeaderContentId;
    private readonly ulong expectedLocalContentId;
    private readonly DadPartyTeardownMutationMode mutationMode;
    private readonly DateTime startedAtUtc;
    private DateTime nextAttemptUtc;
    private int commandAttempts;
    private DadPromptObservation lastPrompt;
    private bool commandSent;
    private int approvedCommandAttempt;
    private int partyMenuCallbackAttempts;
    private DateTime? soloConfirmedSinceUtc;
    private readonly bool allowFreshUnprovenPromptApproval;

    public DadPartyTeardownController(
        IEnumerable<ulong> expectedMemberContentIds,
        ulong expectedLeaderContentId,
        DateTime startedAtUtc,
        bool promptVisible,
        string promptIdentity)
        : this(
            expectedMemberContentIds,
            expectedLeaderContentId,
            expectedLeaderContentId,
            DadPartyTeardownMutationMode.DisbandAsLeader,
            startedAtUtc,
            promptVisible,
            promptIdentity,
            promptReady: false,
            promptText: string.Empty,
            allowFreshUnprovenPromptApproval: false)
    {
    }

    public DadPartyTeardownController(
        IEnumerable<ulong> expectedMemberContentIds,
        ulong expectedLeaderContentId,
        ulong expectedLocalContentId,
        DadPartyTeardownMutationMode mutationMode,
        DateTime startedAtUtc,
        bool promptVisible,
        string promptIdentity,
        bool promptReady = false,
        string promptText = "",
        bool allowFreshUnprovenPromptApproval = false)
    {
        expectedMembers = expectedMemberContentIds.Where(static id => id != 0).ToHashSet();
        this.expectedLeaderContentId = expectedLeaderContentId;
        this.expectedLocalContentId = expectedLocalContentId;
        this.mutationMode = mutationMode;
        this.startedAtUtc = startedAtUtc;
        this.allowFreshUnprovenPromptApproval = allowFreshUnprovenPromptApproval;
        lastPrompt = new DadPromptObservation(
            promptVisible,
            promptReady,
            promptIdentity,
            promptText,
            SoleReadyPrompt: promptVisible);
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
        if (observation.LocalContentId == 0 ||
            expectedLocalContentId == 0 ||
            observation.LocalContentId != expectedLocalContentId)
        {
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Fail,
                "The local character no longer matches the exact frozen teardown identity.");
        }

        if (mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader &&
            (expectedLeaderContentId == 0 ||
             observation.LocalContentId != expectedLeaderContentId ||
             (!partyStateConfirmsSolo && observation.PartyLeaderContentId != expectedLeaderContentId)))
        {
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Fail,
                "The local frozen leader is no longer proven by the authoritative party source; refusing teardown mutation.");
        }

        if (mutationMode == DadPartyTeardownMutationMode.LeaveAsFollower &&
            (!expectedMembers.Contains(expectedLocalContentId) ||
             (!partyStateConfirmsSolo &&
              observation.PartyLeaderContentId != 0 &&
              observation.PartyLeaderContentId != expectedLeaderContentId)))
        {
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Fail,
                "The follower party authority no longer matches the exact frozen Slot1 leader.");
        }

        if (partyStateConfirmsSolo &&
            (mutationMode == DadPartyTeardownMutationMode.LeaveAsFollower || expectedMembers.Count <= 1) &&
            !commandSent)
        {
            return new DadPartyTeardownDecision(DadPartyTeardownAction.Complete, "Party teardown complete; the local character is already solo.");
        }

        if (observation.NowUtc - startedAtUtc >= Timeout)
        {
            var operation = mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader
                ? "breakup"
                : "leave";
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Fail,
                $"Party teardown timed out after {commandAttempts} {operation} command attempt(s).");
        }

        var currentPrompt = new DadPromptObservation(
            observation.PromptVisible,
            observation.PromptReady,
            observation.PromptIdentity,
            observation.PromptText,
            observation.PromptVisible && !observation.OtherReadyPromptVisible);
        var promptBaseline = lastPrompt;
        lastPrompt = currentPrompt;

        if (observation.IsInDuty ||
            observation.IsQueued ||
            (mutationMode == DadPartyTeardownMutationMode.LeaveAsFollower && !observation.IsWorldStable))
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for out-of-duty, not-queued teardown state.");

        var unexpectedMembers = actualMembers.Where(member => !expectedMembers.Contains(member)).ToArray();
        if (!partyStateConfirmsSolo && unexpectedMembers.Length > 0)
        {
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Fail,
                $"Party membership contains unexpected Content ID(s) {string.Join(",", unexpectedMembers)}; refusing teardown mutation.");
        }

        var soloCompletionAllowed = commandSent &&
                                    partyStateConfirmsSolo &&
                                    (mutationMode == DadPartyTeardownMutationMode.LeaveAsFollower ||
                                     approvedCommandAttempt > 0);
        if (soloCompletionAllowed)
        {
            soloConfirmedSinceUtc ??= observation.NowUtc;
            if (observation.NowUtc - soloConfirmedSinceUtc.Value < SoloConfirmationInterval)
            {
                return new DadPartyTeardownDecision(
                    DadPartyTeardownAction.None,
                    "Party absence observed; waiting for sustained cross-world-safe solo confirmation.");
            }

            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.Complete,
                mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader
                    ? "Party teardown complete; cross-world party state is inactive and PartyList remained solo after prompt approval."
                    : "Follower party teardown complete; authoritative party state remained solo after the leave mutation.");
        }

        if (observation.PromptVisible)
        {
            var promptOperation = mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader
                ? DadPromptOperationKind.PartyDisbandTeardown
                : DadPromptOperationKind.PartyLeaveTeardown;
            var operationKey =
                $"party-teardown|{mutationMode}|{expectedLocalContentId}|{expectedLeaderContentId}";
            var promptDecision = DadPromptOwnershipRules.Evaluate(new DadPromptApprovalRequest(
                promptOperation,
                operationKey,
                operationKey,
                commandAttempts,
                commandAttempts,
                approvedCommandAttempt,
                promptBaseline,
                currentPrompt,
                string.Empty,
                allowFreshUnprovenPromptApproval));
            if (!commandSent || !promptDecision.CanApprove)
            {
                return new DadPartyTeardownDecision(
                    DadPartyTeardownAction.None,
                    $"The party teardown confirmation will not be approved: {promptDecision.Summary}");
            }

            approvedCommandAttempt = commandAttempts;
            var operation = mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader
                ? "breakup"
                : "leave";
            return new DadPartyTeardownDecision(
                DadPartyTeardownAction.ApprovePrompt,
                $"Approving the newly appeared {operation} confirmation associated with command attempt {commandAttempts}/{MaximumAttempts}.",
                promptDecision.UsedOverride,
                promptDecision.Summary);
        }

        soloConfirmedSinceUtc = null;

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

        if (observation.NowUtc < nextAttemptUtc || commandAttempts >= MaximumAttempts)
            return new DadPartyTeardownDecision(DadPartyTeardownAction.None, "Waiting for the guarded breakup attempt or confirmation window.");

        commandAttempts++;
        commandSent = true;
        nextAttemptUtc = observation.NowUtc + AttemptThrottle;
        var commandKind = mutationMode == DadPartyTeardownMutationMode.DisbandAsLeader
            ? "breakup"
            : "leave";
        return new DadPartyTeardownDecision(
            DadPartyTeardownAction.SendBreakup,
            $"Sending guarded party {commandKind} command attempt {commandAttempts}/{MaximumAttempts}.");
    }

}
