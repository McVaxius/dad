namespace dad.Services;

internal enum DadAlliancePfCreateCycleOutcome
{
    InProgress,
    Succeeded,
    Blocked,
}

internal enum DadAlliancePfCreateCycleDecision
{
    Continue,
    Complete,
    RestartOnce,
    RemainBlocked,
}

/// <summary>
/// Owns the outer, bounded recovery around the unchanged one-shot Create flow.
/// </summary>
internal sealed class DadAlliancePartyFinderCreateCycleCoordinator
{
    private readonly Func<int> passcodeFactory;
    private bool stopped;

    public DadAlliancePartyFinderCreateCycleCoordinator(
        Func<int>? passcodeFactory = null)
    {
        this.passcodeFactory =
            passcodeFactory ??
            (() => DadAlliancePartyFinderRules.GeneratePasscode());
    }

    public int Cycle { get; private set; } = 1;

    public bool RecoveryUsed => Cycle > 1;

    public void Reset()
    {
        Cycle = 1;
        stopped = false;
    }

    public void Stop()
        => stopped = true;

    public int GenerateFreshPasscode(int previousPasscode)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = passcodeFactory();
            if (candidate != previousPasscode)
                return candidate;
        }

        return previousPasscode == 9999
            ? 1000
            : Math.Clamp(previousPasscode + 1, 1000, 9999);
    }

    public DadAlliancePfCreateCycleDecision Observe(
        DadAlliancePfCreateCycleOutcome outcome,
        bool activeRecruitment)
    {
        if (outcome == DadAlliancePfCreateCycleOutcome.Succeeded)
            return DadAlliancePfCreateCycleDecision.Complete;
        if (outcome != DadAlliancePfCreateCycleOutcome.Blocked)
            return DadAlliancePfCreateCycleDecision.Continue;
        if (stopped || activeRecruitment || RecoveryUsed)
            return DadAlliancePfCreateCycleDecision.RemainBlocked;

        Cycle = 2;
        return DadAlliancePfCreateCycleDecision.RestartOnce;
    }
}
