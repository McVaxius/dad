using System.Text.Json;

namespace dad.Services;

// Session-facing AutoDuty compatibility settings, independent of saved ADS preferences.
internal sealed class DadDutyLootSettings
{
    private bool treasure = true;
    private bool bossOnly;
    private string method = "AutoDuty";
    private bool requestOpen = true;
    internal bool Configured { get; private set; }
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    internal string Failure => string.Join("; ", errors.Values);
    internal const string InactiveJson = "{\"schemaVersion\":1,\"active\":false}";

    internal string? Get(string key, bool beginRequest = true)
    {
        var value = key.ToLowerInvariant() switch
        {
            "loottreasure" => treasure.ToString(),
            "lootbosstreasureonly" => bossOnly.ToString(),
            "lootmethodenum" => method,
            _ => null,
        };
        // Wiggly captures loot values before a new force request. Its later
        // restore is write-only and must not arm another session, even when
        // the captured values and forced values are identical.
        if (value != null && beginRequest) requestOpen = true;
        return value;
    }

    internal bool Set(string key, string value)
    {
        var normalized = key.ToLowerInvariant();
        if (Get(normalized, beginRequest: false) == null) return false;
        if (normalized == "lootmethodenum")
        {
            var canonical = new[] { "AutoDuty", "Pandora", "All" }
                .FirstOrDefault(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                errors[normalized] = $"Invalid LootMethodEnum '{value}'; expected AutoDuty, Pandora or All.";
                return true;
            }
            method = canonical;
        }
        else
        {
            if (!bool.TryParse(value, out var parsed))
            {
                errors[normalized] = $"Invalid {key} '{value}'; expected True or False.";
                return true;
            }
            if (normalized == "loottreasure") treasure = parsed;
            else bossOnly = parsed;
        }
        if (errors.Remove(normalized)) requestOpen = true;
        Configured |= requestOpen;
        return true;
    }

    internal string BuildPolicy(string sessionId, uint territoryTypeId, uint contentFinderConditionId)
    {
        var policy = Configured ? JsonSerializer.Serialize(new
        {
            schemaVersion = 1, active = true, sessionId, territoryTypeId, contentFinderConditionId,
            lootTreasure = treasure, lootBossTreasureOnly = bossOnly, lootMethodEnum = method,
        }) : InactiveJson;
        EndRequest();
        return policy;
    }

    internal void EndRequest()
    {
        Configured = false;
        requestOpen = false;
    }
}
