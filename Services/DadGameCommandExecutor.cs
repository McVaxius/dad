namespace dad.Services;

internal interface IDadGameCommandExecutor
{
    bool TryExecute(string command, out string error);
}

internal sealed class DadAlliancePartyFinderCommandDispatcher
{
    internal const string PartyFinderCommand = "/pfinder";

    private readonly IDadGameCommandExecutor executor;

    public DadAlliancePartyFinderCommandDispatcher(IDadGameCommandExecutor executor)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public DadAlliancePfCreateActionResult TryExecute(string successSummary)
    {
        if (executor.TryExecute(PartyFinderCommand, out var error))
            return new DadAlliancePfCreateActionResult(true, successSummary);

        return new DadAlliancePfCreateActionResult(
            false,
            string.IsNullOrWhiteSpace(error)
                ? "The native /pfinder command was rejected."
                : error,
            error);
    }
}
