using System.Globalization;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using dad.Models;

namespace dad.Services;

public sealed class DadXadbClient
{
    private const string ReadyChannel = "XA.Database.IsReady";
    private const string RefreshChannel = "XA.Database.Refresh";
    private const string SaveChannel = "XA.Database.Save";
    private const string SummaryChannel = "XA.Database.GetCharacterSummaryJson";
    private const string AccountCharacterListChannel = "XA.Database.GetAccountCharacterListJson";

    private readonly ICallGateSubscriber<bool> isReadySubscriber;
    private readonly ICallGateSubscriber<object> refreshSubscriber;
    private readonly ICallGateSubscriber<object> saveSubscriber;
    private readonly ICallGateSubscriber<string> summarySubscriber;
    private readonly ICallGateSubscriber<string> accountCharacterListSubscriber;
    private readonly IPluginLog log;

    public DadXadbClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isReadySubscriber = pluginInterface.GetIpcSubscriber<bool>(ReadyChannel);
        refreshSubscriber = pluginInterface.GetIpcSubscriber<object>(RefreshChannel);
        saveSubscriber = pluginInterface.GetIpcSubscriber<object>(SaveChannel);
        summarySubscriber = pluginInterface.GetIpcSubscriber<string>(SummaryChannel);
        accountCharacterListSubscriber = pluginInterface.GetIpcSubscriber<string>(AccountCharacterListChannel);
        this.log = log;
    }

    public DadXadbStatus Inspect()
    {
        var status = BuildAvailability();
        if (!status.IsReady)
            return status;

        PopulateSummary(status);
        return status;
    }

    public DadXadbStatus Refresh()
    {
        var status = BuildAvailability();
        if (!status.IsReady)
            return status;

        if (TryInvokeAction(refreshSubscriber, "refresh", status))
            status.LastRefreshUtc = DateTime.UtcNow;

        PopulateSummary(status);
        return status;
    }

    public DadXadbStatus Save()
    {
        var status = BuildAvailability();
        if (!status.IsReady)
            return status;

        if (TryInvokeAction(refreshSubscriber, "refresh", status))
            status.LastRefreshUtc = DateTime.UtcNow;

        if (TryInvokeAction(saveSubscriber, "save", status))
            status.LastSaveUtc = DateTime.UtcNow;

        PopulateSummary(status);
        return status;
    }

    public DadAccountRosterCatalog GetAccountCharacterList()
    {
        var status = BuildAvailability();
        var catalog = new DadAccountRosterCatalog
        {
            GeneratedAtUtc = DateTime.UtcNow,
            IsFullRosterAvailable = false,
            Summary = status.LastStatus,
        };

        if (!status.IsReady)
        {
            catalog.Warnings.AddRange(status.Warnings.Count == 0 ? [status.LastStatus] : status.Warnings);
            return catalog;
        }

        try
        {
            var json = accountCharacterListSubscriber.InvokeFunc();
            if (string.IsNullOrWhiteSpace(json))
            {
                catalog.Warnings.Add("XADB account roster IPC returned empty JSON.");
                catalog.Summary = "XADB account roster empty.";
                return catalog;
            }

            using var doc = JsonDocument.Parse(json);
            PopulateAccountRosterCatalog(catalog, doc.RootElement);
            catalog.Summary = catalog.IsFullRosterAvailable
                ? $"XADB account roster ready: {catalog.Characters.Count} character(s)."
                : catalog.Characters.Count > 0
                    ? "XADB account roster JSON did not advertise full roster support."
                    : "XADB account roster JSON contained no characters.";
            return catalog;
        }
        catch (Exception ex)
        {
            catalog.Warnings.Add("XADB account roster IPC unavailable or unreadable.");
            catalog.Summary = "XADB account roster unavailable.";
            log.Debug(ex, "[dad] XADB account roster IPC failed.");
            return catalog;
        }
    }

    public DadRosterRefreshResultDto RefreshAndSaveForRosterUpdate(
        DadRosterRefreshCommandDto command,
        DadParticipantSnapshot snapshot)
    {
        var result = new DadRosterRefreshResultDto
        {
            CommandId = command.CommandId,
            AccountKey = command.AccountKey,
            CharacterKey = command.CharacterKey,
            ContentId = command.ContentId,
            DryRun = command.DryRun,
            Snapshot = snapshot.Clone(),
        };

        if (command.DryRun)
        {
            result.Accepted = true;
            result.Success = true;
            result.RefreshedAtUtc = DateTime.UtcNow;
            result.Summary = $"Dry-run roster refresh for {command.CharacterKey}.";
            return result;
        }

        var status = command.SaveAfterRefresh ? Save() : Refresh();
        result.XadbStatus = status;
        result.Accepted = true;
        result.Success = status.IsReady && status.Warnings.Count == 0;
        result.RefreshedAtUtc = result.Success ? DateTime.UtcNow : null;
        result.Summary = status.LastStatus;
        return result;
    }

    private DadXadbStatus BuildAvailability()
    {
        var status = new DadXadbStatus();

        try
        {
            status.IsReady = isReadySubscriber.InvokeFunc();
            status.Availability = status.IsReady ? "Ready" : "Unavailable";
            status.LastStatus = status.IsReady
                ? "XADB ready."
                : "XADB reported not ready.";
        }
        catch (Exception ex)
        {
            status.IsReady = false;
            status.Availability = "Unavailable";
            status.LastStatus = "XADB IPC unavailable.";
            status.Warnings.Add("XADB IPC unavailable.");
            log.Debug(ex, "[dad] XADB readiness check failed.");
        }

        return status;
    }

    private bool TryInvokeAction(ICallGateSubscriber<object> subscriber, string actionName, DadXadbStatus status)
    {
        try
        {
            subscriber.InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            var warning = $"XADB {actionName} failed.";
            status.Warnings.Add(warning);
            status.LastStatus = warning;
            log.Warning(ex, "[dad] XADB {ActionName} failed.", actionName);
            return false;
        }
    }

    private void PopulateSummary(DadXadbStatus status)
    {
        try
        {
            var json = summarySubscriber.InvokeFunc();
            status.RawSummaryJson = json ?? string.Empty;

            if (string.IsNullOrWhiteSpace(json))
            {
                status.LastStatus = "XADB ready, summary empty.";
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            status.ContentId = ReadUInt64(root, "contentId", "ContentId");
            status.WorldId = ReadNullableUInt32(root, "worldId", "WorldId");
            status.CharacterName = ReadString(root, "characterName", "name");
            status.WorldName = ReadString(root, "worldName", "world");
            status.DataCenterName = ReadString(root, "dataCenterName", "datacenter");
            status.CurrentJobId = ReadNullableUInt32(root, "currentJobId", "classJobId", "jobId");
            status.CurrentJobAbbrev = ReadString(root, "currentJobAbbrev", "jobAbbrev", "currentJob");
            status.CurrentLevel = ReadNullableInt32(root, "currentLevel", "level");
            status.SnapshotVersion = ReadNullableInt32(root, "snapshotVersion", "characterSummaryJsonVersion", "summaryVersion");
            status.SnapshotQuality = ReadString(root, "snapshotQuality");
            status.SnapshotUtc = ReadNullableDateTime(root, "updatedUtc", "snapshotUtc", "capturedAtUtc", "lastSaveUtc");
            status.JobLevels = ReadJobLevels(root);

            if (TryGetProperty(root, out var worldIdElement, "worldId", "WorldId"))
                status.WorldId ??= ReadUInt32(worldIdElement);

            if (TryGetProperty(root, out var dataCenterElement, "dataCenterId", "DataCenterId"))
                status.DataCenterId = ReadUInt32(dataCenterElement);

            if (string.IsNullOrWhiteSpace(status.LastStatus))
                status.LastStatus = "XADB ready.";

            status.LastStatus = status.SnapshotVersion.HasValue
                ? $"XADB ready. Summary v{status.SnapshotVersion}."
                : "XADB ready.";

            if (!string.IsNullOrWhiteSpace(status.SnapshotQuality))
                status.LastStatus = $"{status.LastStatus} Quality {status.SnapshotQuality}.";
        }
        catch (Exception ex)
        {
            status.Warnings.Add("XADB summary JSON unreadable.");
            status.LastStatus = "XADB ready, summary JSON unreadable.";
            log.Warning(ex, "[dad] Failed to parse XADB summary JSON.");
        }
    }

    private static void PopulateAccountRosterCatalog(DadAccountRosterCatalog catalog, JsonElement root)
    {
        catalog.Version = ReadNullableInt32(root, "version", "rosterVersion", "accountCharacterListVersion") ?? 1;
        catalog.GeneratedAtUtc = ReadNullableDateTime(root, "generatedAtUtc", "updatedUtc", "snapshotUtc") ?? DateTime.UtcNow;
        catalog.IsFullRosterAvailable = ReadNullableBool(root, "isFullRosterAvailable", "fullRosterAvailable") ?? false;

        if (TryGetProperty(root, out var warningsElement, "warnings") &&
            warningsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var warningElement in warningsElement.EnumerateArray())
            {
                var warning = warningElement.ValueKind == JsonValueKind.String
                    ? warningElement.GetString()
                    : warningElement.ToString();
                if (!string.IsNullOrWhiteSpace(warning))
                    catalog.Warnings.Add(warning);
            }
        }

        var accountsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetProperty(root, out var accountsElement, "accounts", "accountList") &&
            accountsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var accountElement in accountsElement.EnumerateArray())
            {
                if (accountElement.ValueKind != JsonValueKind.Object)
                    continue;

                var accountKey = ReadString(accountElement, "accountKey", "accountId", "id", "key");
                var accountAlias = ReadString(accountElement, "accountAlias", "alias", "displayName", "name");
                if (!string.IsNullOrWhiteSpace(accountKey))
                    accountsByKey[accountKey] = accountAlias;

                if (TryGetProperty(accountElement, out var nestedCharacters, "characters", "characterList") &&
                    nestedCharacters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var characterElement in nestedCharacters.EnumerateArray())
                        AddRosterCharacter(catalog, characterElement, accountKey, accountAlias);
                }
            }
        }

        if (TryGetProperty(root, out var charactersElement, "characters", "characterList", "roster") &&
            charactersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var characterElement in charactersElement.EnumerateArray())
            {
                var accountKey = ReadString(characterElement, "accountKey", "accountId", "account");
                accountsByKey.TryGetValue(accountKey, out var accountAlias);
                AddRosterCharacter(catalog, characterElement, accountKey, accountAlias ?? string.Empty);
            }
        }

        catalog.Characters = catalog.Characters
            .Where(static character => !character.CharacterKey.IsEmpty || character.ContentId != 0)
            .DistinctBy(DadRosterIdentity.BuildKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static character => character.AccountAlias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.AccountKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static character => character.CharacterKey.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddRosterCharacter(
        DadAccountRosterCatalog catalog,
        JsonElement characterElement,
        string accountKey,
        string accountAlias)
    {
        if (characterElement.ValueKind != JsonValueKind.Object)
            return;

        var characterName = ReadString(characterElement, "characterName", "name");
        var worldName = ReadString(characterElement, "worldName", "world");
        var characterKey = ReadString(characterElement, "characterKey", "key");
        if (string.IsNullOrWhiteSpace(characterKey) &&
            !string.IsNullOrWhiteSpace(characterName) &&
            !string.IsNullOrWhiteSpace(worldName))
        {
            characterKey = $"{characterName.Trim()}@{worldName.Trim()}";
        }

        var resolvedAccountKey = ReadString(characterElement, "accountKey", "accountId", "account");
        if (string.IsNullOrWhiteSpace(resolvedAccountKey))
            resolvedAccountKey = accountKey;

        var resolvedAccountAlias = ReadString(characterElement, "accountAlias", "alias", "accountName");
        if (string.IsNullOrWhiteSpace(resolvedAccountAlias))
            resolvedAccountAlias = accountAlias;

        var lastSnapshotUtc = ReadNullableDateTime(
            characterElement,
            "lastSnapshotUtc",
            "snapshotUtc",
            "updatedUtc",
            "capturedAtUtc",
            "lastSaveUtc");

        var rosterCharacter = new DadRosterCharacter
        {
            AccountKey = new DadAccountKey(resolvedAccountKey),
            AccountAlias = resolvedAccountAlias,
            CharacterKey = new DadCharacterKey(characterKey),
            ContentId = ReadUInt64(characterElement, "contentId", "ContentId"),
            CharacterName = characterName,
            WorldId = ReadNullableUInt32(characterElement, "worldId", "WorldId"),
            WorldName = worldName,
            DataCenterId = ReadNullableUInt32(characterElement, "dataCenterId", "DataCenterId"),
            DataCenterName = ReadString(characterElement, "dataCenterName", "datacenter", "dc"),
            LastSnapshotUtc = lastSnapshotUtc,
            JobLevels = ReadJobLevels(characterElement),
            CurrentJobId = ReadNullableUInt32(characterElement, "currentJobId", "classJobId", "jobId"),
            CurrentJobAbbrev = ReadString(characterElement, "currentJobAbbrev", "jobAbbrev", "currentJob"),
            CurrentLevel = ReadNullableInt32(characterElement, "currentLevel", "level"),
            SnapshotQuality = ReadString(characterElement, "snapshotQuality", "quality"),
            SnapshotVersion = ReadNullableInt32(characterElement, "snapshotVersion", "characterSummaryJsonVersion", "summaryVersion"),
            XadbReady = true,
            IsCurrent = ReadNullableBool(characterElement, "isCurrent", "current") ?? false,
            MapEligible = ReadNullableBool(characterElement, "mapEligible", "treasureMapEligible", "eligibleForMaps"),
            MapEligibilitySummary = ReadString(characterElement, "mapEligibilitySummary", "mapStatus", "treasureMapStatus"),
            Source = DadCharacterSource.XadbOnly,
        };

        rosterCharacter.IsStale = lastSnapshotUtc.HasValue && DateTime.UtcNow - lastSnapshotUtc.Value > TimeSpan.FromHours(72);
        catalog.Characters.Add(rosterCharacter);
    }

    private static Dictionary<uint, int> ReadJobLevels(JsonElement root)
    {
        if (!TryGetProperty(root, out var jobsElement, "jobLevels", "jobs", "jobLevelsById"))
            return [];

        var levels = new Dictionary<uint, int>();

        if (jobsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in jobsElement.EnumerateObject())
            {
                if (!TryParseUInt32(property.Name, out var jobId))
                    continue;

                var level = ReadNullableInt32(property.Value);
                if (level.HasValue)
                    levels[jobId] = level.Value;
            }

            return levels;
        }

        if (jobsElement.ValueKind != JsonValueKind.Array)
            return levels;

        foreach (var entry in jobsElement.EnumerateArray())
        {
            var jobId = ReadNullableUInt32(entry, "jobId", "classJobId", "id");
            var level = ReadNullableInt32(entry, "level", "currentLevel", "jobLevel");
            if (jobId.HasValue && level.HasValue)
                levels[jobId.Value] = level.Value;
        }

        return levels;
    }

    private static string ReadString(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var property, propertyNames))
            return string.Empty;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            _ => string.Empty,
        };
    }

    private static uint? ReadNullableUInt32(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var property, propertyNames))
            return null;

        return ReadUInt32(property);
    }

    private static uint? ReadUInt32(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var number))
            return number;

        if (element.ValueKind == JsonValueKind.String && TryParseUInt32(element.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static ulong ReadUInt64(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var property, propertyNames))
            return 0;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out var number))
            return number;

        if (property.ValueKind == JsonValueKind.String && TryParseUInt64(property.GetString(), out var parsed))
            return parsed;

        return 0;
    }

    private static int? ReadNullableInt32(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var property, propertyNames))
            return null;

        return ReadNullableInt32(property);
    }

    private static int? ReadNullableInt32(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            return number;

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static DateTime? ReadNullableDateTime(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var property, propertyNames))
            return null;

        if (property.ValueKind != JsonValueKind.String)
            return null;

        return DateTime.TryParse(
            property.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool? ReadNullableBool(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var property, propertyNames))
            return null;

        if (property.ValueKind == JsonValueKind.True)
            return true;

        if (property.ValueKind == JsonValueKind.False)
            return false;

        if (property.ValueKind == JsonValueKind.String &&
            bool.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement root, out JsonElement property, params string[] propertyNames)
    {
        foreach (var candidate in propertyNames)
        {
            if (root.TryGetProperty(candidate, out property))
                return true;

            foreach (var existing in root.EnumerateObject())
            {
                if (string.Equals(existing.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    property = existing.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static bool TryParseUInt32(string? value, out uint parsed)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                return true;
            }

            if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return true;
        }

        parsed = 0;
        return false;
    }

    private static bool TryParseUInt64(string? value, out ulong parsed)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                return true;
            }

            if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return true;
        }

        parsed = 0;
        return false;
    }
}
