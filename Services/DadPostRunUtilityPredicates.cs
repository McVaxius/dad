namespace dad.Services;

public static class DadPostRunUtilityPredicates
{
    public static bool IsGearCoffer(string itemName, string itemDescription = "")
    {
        var name = itemName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!name.Contains("Coffer", StringComparison.OrdinalIgnoreCase))
            return false;

        var description = itemDescription?.Trim() ?? string.Empty;
        return name.Contains("Gear", StringComparison.OrdinalIgnoreCase)
               || description.Contains("gear", StringComparison.OrdinalIgnoreCase)
               || description.Contains("equipment", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTripleTriadCard(string itemName, string itemDescription = "")
    {
        var name = itemName?.Trim() ?? string.Empty;
        var description = itemDescription?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.EndsWith("Card", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Triple Triad", StringComparison.OrdinalIgnoreCase)
               || description.Contains("Triple Triad", StringComparison.OrdinalIgnoreCase);
    }
}
