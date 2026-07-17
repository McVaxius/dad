namespace dad.Models;

internal readonly record struct DadXadbRosterSuccessSignature(
    int RowCount,
    int RosterVersion,
    int? ContractVersion,
    bool IsFullRosterAvailable);

internal enum DadXadbRosterLogTransition
{
    FirstSuccess = 0,
    UnchangedSuccess = 1,
    ChangedSuccess = 2,
    Failure = 3,
    RecoveredSuccess = 4,
}

internal sealed class DadXadbRosterSuccessLogTracker
{
    private readonly object gate = new();
    private DadXadbRosterSuccessSignature? lastSuccess;
    private bool failureSinceLastSuccess;

    public DadXadbRosterLogTransition RecordSuccess(DadXadbRosterSuccessSignature signature)
    {
        lock (gate)
        {
            var transition = failureSinceLastSuccess
                ? DadXadbRosterLogTransition.RecoveredSuccess
                : !lastSuccess.HasValue
                    ? DadXadbRosterLogTransition.FirstSuccess
                    : lastSuccess.Value == signature
                        ? DadXadbRosterLogTransition.UnchangedSuccess
                        : DadXadbRosterLogTransition.ChangedSuccess;
            lastSuccess = signature;
            failureSinceLastSuccess = false;
            return transition;
        }
    }

    public DadXadbRosterLogTransition RecordFailure()
    {
        lock (gate)
        {
            failureSinceLastSuccess = true;
            return DadXadbRosterLogTransition.Failure;
        }
    }

    public static bool ShouldWriteInformation(DadXadbRosterLogTransition transition)
        => transition is DadXadbRosterLogTransition.FirstSuccess
            or DadXadbRosterLogTransition.ChangedSuccess
            or DadXadbRosterLogTransition.RecoveredSuccess;
}
