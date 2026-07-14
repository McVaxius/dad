using System.Text.Json;
using dad.Models;

namespace dad.Services;

public static class DadAdsConfigurationPatchRules
{
    private static readonly string[] RegistrableCategoryNames =
    [
        "lootRegistrableNeedingEnabled",
        "lootRegistrableMountsEnabled",
        "lootRegistrableMinionsEnabled",
        "lootRegistrableFashionAccessoriesEnabled",
        "lootRegistrableFacewearEnabled",
        "lootRegistrableOrchestrionRollsEnabled",
        "lootRegistrableFadedOrchestrionCopiesEnabled",
        "lootRegistrableEmotesHairstylesEnabled",
        "lootRegistrableBardingsEnabled",
        "lootRegistrableTripleTriadCardsEnabled",
    ];

    public static string BuildPatchJson(DadAdsLootMode? mode)
    {
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in RegistrableCategoryNames)
            patch[name] = true;

        if (mode is DadAdsLootMode.Need or DadAdsLootMode.Greed or DadAdsLootMode.Pass)
            patch["lootMode"] = mode.Value.ToString();

        return JsonSerializer.Serialize(patch);
    }

    public static bool TryParseResponse(string? json, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "ADS returned an empty configuration-patch response.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("success", out var success) &&
                success.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                success.GetBoolean())
            {
                return true;
            }

            reason = root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("message", out var message) &&
                     message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? "ADS rejected the configuration patch."
                : "ADS rejected the configuration patch.";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"ADS returned malformed configuration-patch JSON: {ex.Message}";
            return false;
        }
    }

    public static bool TryEvaluateReadiness(
        bool installedMetadataReportsLoaded,
        string? responseJson,
        string? invocationFailure,
        out string reason)
    {
        if (!string.IsNullOrWhiteSpace(invocationFailure))
        {
            reason = installedMetadataReportsLoaded
                ? $"ADS is loaded according to diagnostic metadata, but ADS.PatchConfigurationJson is missing or stale: {invocationFailure}"
                : $"ADS is unloaded and ADS.PatchConfigurationJson is unavailable: {invocationFailure}";
            return false;
        }

        if (TryParseResponse(responseJson, out reason))
            return true;

        reason = $"ADS.PatchConfigurationJson rejected the required configuration patch: {reason}";
        return false;
    }
}
