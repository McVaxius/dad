using System.Text.Json;

namespace dad.Services;

internal sealed record DadAdsDutyMaturity(bool Success, int? Level, string ClearanceStatus, string Message)
{
    internal bool Eligible => Success && Level is >= 1 and <= 3;
    internal string Summary => Success ? $"ADS maturity {Level} ({ClearanceStatus}); QST requires > 0." : Message;

    internal static DadAdsDutyMaturity Parse(string json, uint territoryTypeId, uint contentFinderConditionId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("territoryTypeId").GetUInt32() != territoryTypeId ||
                root.GetProperty("contentFinderConditionId").GetUInt32() != contentFinderConditionId)
                return Unavailable("ADS maturity response has an unsupported schema or mismatched territory/CFC.");
            if (!root.GetProperty("success").GetBoolean())
                return Unavailable("ADS maturity is unavailable for the requested territory/CFC.");
            var level = root.GetProperty("clearanceLevel").GetInt32();
            var status = root.GetProperty("clearanceStatus").GetString() ?? string.Empty;
            var expected = level switch
            {
                0 => "NotCleared", 1 => "OnePlayerUnsyncCleared", 2 => "OnePlayerDutySupport",
                3 => "FourPlayerSyncCleared", _ => string.Empty,
            };
            if (expected.Length == 0 || status != expected ||
                root.GetProperty("eligibleForQuest").GetBoolean() != (level > 0))
                return Unavailable("ADS maturity response contains invalid or contradictory clearance data.");
            return new(true, level, status, string.Empty);
        }
        catch (Exception ex)
        {
            return Unavailable($"ADS maturity response is unreadable: {ex.Message}");
        }
    }

    internal static DadAdsDutyMaturity Unavailable(string reason) => new(false, null, string.Empty, reason);
}
