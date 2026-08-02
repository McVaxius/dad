namespace dad.Services;

internal enum DadPromptOperationKind
{
    AlliancePartyFinderJoin,
    PartyInvitationAcceptance,
    ParticipantPartyDeparture,
    PartyDisbandTeardown,
    PartyLeaveTeardown,
    AllianceRecruitmentCleanup,
}

internal enum DadPromptApprovalKind
{
    Rejected,
    Exact,
    Override,
}

internal readonly record struct DadPromptObservation(
    bool Visible,
    bool Ready,
    string Identity,
    string Text,
    bool SoleReadyPrompt);

internal readonly record struct DadPromptApprovalRequest(
    DadPromptOperationKind Operation,
    string FrozenOperationKey,
    string CurrentOperationKey,
    int FrozenAttempt,
    int CurrentAttempt,
    int ApprovedAttempt,
    DadPromptObservation Baseline,
    DadPromptObservation Current,
    string ExpectedSubject,
    bool AllowFreshUnprovenPromptApproval);

internal readonly record struct DadPromptApprovalDecision(
    DadPromptApprovalKind Kind,
    string Summary)
{
    public bool CanApprove => Kind is DadPromptApprovalKind.Exact or DadPromptApprovalKind.Override;
    public bool UsedOverride => Kind == DadPromptApprovalKind.Override;
}

internal static class DadPromptOwnershipRules
{
    public static DadPromptApprovalDecision Evaluate(DadPromptApprovalRequest request)
    {
        if (!request.Current.Visible ||
            !request.Current.Ready ||
            string.IsNullOrWhiteSpace(request.Current.Identity))
        {
            return Reject("The prompt is not visible, ready, and identity-bearing.");
        }

        if (string.IsNullOrWhiteSpace(request.FrozenOperationKey) ||
            !string.Equals(
                request.FrozenOperationKey.Trim(),
                request.CurrentOperationKey?.Trim(),
                StringComparison.Ordinal))
        {
            return Reject("The frozen prompt operation context changed.");
        }

        if (request.FrozenAttempt <= 0 ||
            request.FrozenAttempt != request.CurrentAttempt)
        {
            return Reject("The prompt no longer belongs to the current command attempt.");
        }

        if (request.ApprovedAttempt == request.CurrentAttempt)
            return Reject("This command attempt already approved a prompt.");

        if (request.Baseline.Visible &&
            string.Equals(
                request.Baseline.Identity,
                request.Current.Identity,
                StringComparison.Ordinal))
        {
            return Reject("The prompt identity was already visible before the current command attempt.");
        }

        if (IsOperationRelevantText(
                request.Operation,
                request.Current.Text,
                request.ExpectedSubject))
        {
            return new DadPromptApprovalDecision(
                DadPromptApprovalKind.Exact,
                "The fresh prompt text and frozen operation context match exactly.");
        }

        if (!request.AllowFreshUnprovenPromptApproval)
        {
            return Reject("The fresh prompt text is unreadable or does not match the frozen operation context.");
        }

        if (!request.Current.SoleReadyPrompt)
            return Reject("The prompt override requires one sole ready prompt.");

        return new DadPromptApprovalDecision(
            DadPromptApprovalKind.Override,
            "WARNING: the operator override approved one fresh sole ready prompt without exact text proof.");
    }

    public static bool IsOperationRelevantText(
        DadPromptOperationKind operation,
        string? text,
        string? expectedSubject = null)
    {
        var prompt = text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
            return false;

        var hasParty = prompt.Contains("party", StringComparison.OrdinalIgnoreCase) ||
                       prompt.Contains("alliance", StringComparison.OrdinalIgnoreCase);
        var subjectMatches = string.IsNullOrWhiteSpace(expectedSubject) ||
                             prompt.Contains(expectedSubject.Trim(), StringComparison.Ordinal);
        return operation switch
        {
            DadPromptOperationKind.AlliancePartyFinderJoin or
                DadPromptOperationKind.PartyInvitationAcceptance =>
                hasParty && subjectMatches &&
                (prompt.Contains("join", StringComparison.OrdinalIgnoreCase) ||
                 prompt.Contains("accept", StringComparison.OrdinalIgnoreCase)),
            DadPromptOperationKind.ParticipantPartyDeparture or
                DadPromptOperationKind.PartyLeaveTeardown =>
                hasParty && prompt.Contains("leave", StringComparison.OrdinalIgnoreCase),
            DadPromptOperationKind.PartyDisbandTeardown =>
                hasParty &&
                (prompt.Contains("disband", StringComparison.OrdinalIgnoreCase) ||
                 prompt.Contains("break", StringComparison.OrdinalIgnoreCase)),
            DadPromptOperationKind.AllianceRecruitmentCleanup =>
                prompt.Contains("recruit", StringComparison.OrdinalIgnoreCase) &&
                !prompt.Contains("disband", StringComparison.OrdinalIgnoreCase) &&
                !prompt.Contains("leave the party", StringComparison.OrdinalIgnoreCase) &&
                !prompt.Contains("leave the alliance", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static DadPromptApprovalDecision Reject(string summary)
        => new(DadPromptApprovalKind.Rejected, summary);
}
