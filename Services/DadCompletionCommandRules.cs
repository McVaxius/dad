namespace dad.Services;

public static class DadCompletionCommandRules
{
    public static bool TryNormalizeCustomCommand(
        string? command,
        out string normalized,
        out string reason)
        => TryNormalizeSlashCommand(command, "Completion command", out normalized, out reason);

    public static bool TryNormalizeGrandCompanyHandInCommand(
        string? command,
        out string normalized,
        out string reason)
    {
        if (!TryNormalizeSlashCommand(command, "Grand Company hand-in command", out normalized, out reason))
            return false;

        var rootLength = normalized.IndexOf(' ');
        var root = rootLength < 0 ? normalized : normalized[..rootLength];
        if (!string.Equals(root, "/ays", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
            reason = "Grand Company hand-in command must use the exact /ays command root.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool TryNormalizeCustomCommands(
        IEnumerable<string>? commands,
        out List<string> normalized,
        out string reason)
    {
        normalized = [];
        reason = string.Empty;
        foreach (var command in commands ?? [])
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;
            if (!TryNormalizeCustomCommand(command, out var value, out reason))
            {
                normalized = [];
                return false;
            }
            normalized.Add(value);
        }
        return true;
    }

    private static bool TryNormalizeSlashCommand(
        string? command,
        string label,
        out string normalized,
        out string reason)
    {
        var raw = command ?? string.Empty;
        normalized = string.Empty;
        if (raw.Any(char.IsControl))
        {
            reason = $"{label} must be one line and contain no control characters.";
            return false;
        }

        normalized = raw.Trim();
        if (normalized.Length <= 1 || !normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = string.Empty;
            reason = $"{label} must be a non-empty slash command.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
