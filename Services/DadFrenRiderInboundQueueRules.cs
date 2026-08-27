namespace dad.Services;

internal enum DadFrenRiderProfileApplicationOutcome
{
    None = 0,
    TemporaryApplied = 1,
    PermanentApplied = 2,
    OptedOut = 3,
}

internal readonly record struct DadFrenRiderProfileOwnership(
    Guid ProposalId,
    string SenderIslandId,
    string OwnerId,
    string CharacterId);

internal sealed record DadFrenRiderProfileApplicationResult(
    bool Success,
    DadFrenRiderProfileApplicationOutcome Outcome,
    string SafeCode)
{
    public static DadFrenRiderProfileApplicationResult Failed(string safeCode)
        => new(false, DadFrenRiderProfileApplicationOutcome.None, safeCode);
}

internal static class DadFrenRiderInboundQueueRules
{
    internal static bool IsAllowed(
        bool useFrenRider,
        DadFrenRiderProfileApplicationResult? outcome,
        bool frenRiderLoaded,
        out string blocker)
    {
        blocker = string.Empty;
        if (!useFrenRider)
            return true;
        if (outcome == null || !outcome.Success ||
            outcome.Outcome is not (DadFrenRiderProfileApplicationOutcome.TemporaryApplied or
                DadFrenRiderProfileApplicationOutcome.PermanentApplied or
                DadFrenRiderProfileApplicationOutcome.OptedOut))
        {
            blocker = outcome?.SafeCode ?? "dad-inbound-frenrider-profile-not-applied";
            return false;
        }
        if (outcome.Outcome != DadFrenRiderProfileApplicationOutcome.OptedOut && !frenRiderLoaded)
        {
            blocker = "dad-inbound-frenrider-unavailable";
            return false;
        }

        blocker = outcome.SafeCode;
        return true;
    }
}
