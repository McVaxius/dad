namespace dad.Models;

public static class DadCrewAccountPresentationRules
{
    public static string Format(DadRosterAccountOption option, bool showDetails)
    {
        var accountId = option.AccountKey.Value?.Trim() ?? string.Empty;
        var alias = !string.IsNullOrWhiteSpace(option.AccountAlias)
            ? option.AccountAlias.Trim()
            : !string.IsNullOrWhiteSpace(accountId)
                ? accountId
                : option.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(alias))
            alias = "(account)";

        var label = showDetails &&
                    !string.IsNullOrWhiteSpace(accountId) &&
                    !string.Equals(alias, accountId, StringComparison.OrdinalIgnoreCase)
            ? $"{alias} [{accountId}]"
            : alias;
        return option.OwnerOnline ? label : $"{label} [offline]";
    }
}

public sealed record DadRosterBrowseFilterState(
    string Search,
    string Account,
    string Assigned,
    string Visibility,
    string WorldDc,
    string Source,
    string Client,
    bool StaleOnly)
{
    public static DadRosterBrowseFilterState ShowAccount(DadAccountKey accountKey)
        => new(
            string.Empty,
            accountKey.Value?.Trim() ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false);
}

public static class DadClientNamingRules
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "account",
        "(account)",
        "dad client",
        "client dad",
        "dad",
        "client",
    };

    public static bool TryValidate(string? alias, string? accountId, out string normalizedAlias, out string reason)
    {
        normalizedAlias = alias?.Trim() ?? string.Empty;
        var stableId = accountId?.Trim() ?? string.Empty;
        if (normalizedAlias.Length < 2)
        {
            reason = "Enter a name with at least two characters.";
            return false;
        }

        if (Placeholders.Contains(normalizedAlias) ||
            !string.IsNullOrWhiteSpace(stableId) && string.Equals(normalizedAlias, stableId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Choose a meaningful name instead of the account ID or a generic placeholder.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool IsReady(AccountConfig? account, string? stableAccountId)
        => account != null &&
           string.Equals(account.AccountId, stableAccountId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
           TryValidate(account.AccountAlias, stableAccountId, out _, out _);
}

[Flags]
public enum DadGuideSurface
{
    None = 0,
    Transport = 1,
    Roster = 2,
    Planner = 4,
    Scheduler = 8,
    Profiles = 16,
}

public static class DadGuideSurfaceRules
{
    public static DadGuideSurface RequiredFor(string flow)
        => flow switch
        {
            "Coordinator" or "Client" => DadGuideSurface.Transport,
            "NameDad" => DadGuideSurface.Profiles,
            "FirstPreset" => DadGuideSurface.Planner,
            "Crew" => DadGuideSurface.Roster,
            "Schedule" => DadGuideSurface.Planner | DadGuideSurface.Scheduler,
            _ => DadGuideSurface.None,
        };
}
