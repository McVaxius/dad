using dad.Models;

namespace dad.Services;

// B2: pure helpers that build the compact roster projection carried in the hub publish, and rebuild
// peer catalogs from a received projection. Kept Dalamud-free so the build/merge round-trip is unit-tested.
internal static class DadHubRosterCatalogProjection
{
    // Hard cap on projected rows so a pathological roster cannot blow past the 256 KiB frame cap. The
    // transport additionally trims the projection if the serialized publish approaches MaxFrameBytes.
    public const int DefaultMaxRows = 800;

    public static DadHubRosterCatalogRow ProjectCatalogRow(
        DadWorkerSessionId ownerWorkerSessionId,
        string? ownerClientInstanceId,
        DadRosterCharacter character)
        => new()
        {
            OwnerWorkerSessionId = ownerWorkerSessionId,
            OwnerClientInstanceId = ownerClientInstanceId?.Trim() ?? string.Empty,
            AccountKey = character.AccountKey,
            AccountAlias = character.AccountAlias,
            CharacterKey = character.CharacterKey,
            ContentId = character.ContentId,
            CharacterName = character.CharacterName,
            WorldName = character.WorldName,
            JobLevels = new Dictionary<uint, int>(character.JobLevels),
            CurrentJobId = character.CurrentJobId,
            CurrentJobAbbrev = character.CurrentJobAbbrev,
            CurrentLevel = character.CurrentLevel,
            Source = character.Source,
        };

    // Build the projection from per-owner catalog responses (peer caches + the coordinator's own catalog).
    // Rows are de-duplicated by owner + roster identity and capped at maxRows.
    public static List<DadHubRosterCatalogRow> BuildCatalogRows(
        IEnumerable<DadPeerRosterCatalogResponse> responses,
        int maxRows = DefaultMaxRows)
    {
        var rows = new List<DadHubRosterCatalogRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var response in responses)
        {
            if (response?.Catalog?.Characters == null)
                continue;

            foreach (var character in response.Catalog.Characters)
            {
                if (character == null)
                    continue;
                if (character.ContentId == 0 && string.IsNullOrWhiteSpace(character.CharacterKey.Value))
                    continue;

                var ownerWorkerSessionId = character.SourceWorkerSessionId.IsEmpty
                    ? response.WorkerSessionId
                    : character.SourceWorkerSessionId;
                var ownerClientInstanceId = string.IsNullOrWhiteSpace(character.SourceClientInstanceId)
                    ? response.ClientInstanceId
                    : character.SourceClientInstanceId;

                var key = BuildRowKey(ownerWorkerSessionId, ownerClientInstanceId, character.AccountKey, character.CharacterKey, character.ContentId);
                if (!seen.Add(key))
                    continue;

                rows.Add(ProjectCatalogRow(ownerWorkerSessionId, ownerClientInstanceId, character));
                if (rows.Count >= maxRows)
                    return rows;
            }
        }

        return rows;
    }

    public static DadRosterCharacter BuildRosterCharacter(DadHubRosterCatalogRow row)
        => new()
        {
            AccountKey = row.AccountKey,
            AccountAlias = row.AccountAlias,
            CharacterKey = row.CharacterKey,
            ContentId = row.ContentId,
            CharacterName = row.CharacterName,
            WorldName = row.WorldName,
            JobLevels = new Dictionary<uint, int>(row.JobLevels),
            CurrentJobId = row.CurrentJobId,
            CurrentJobAbbrev = row.CurrentJobAbbrev,
            CurrentLevel = row.CurrentLevel,
            Source = row.Source,
            SourceWorkerSessionId = row.OwnerWorkerSessionId,
            SourceClientInstanceId = row.OwnerClientInstanceId,
            IsCurrent = true,
        };

    // Reconstruct per-owner peer catalog responses from a received projection so the client merge can
    // render peers with no pull. Rows owned by excludeWorkerSessionId (the local client) are skipped.
    public static List<DadPeerRosterCatalogResponse> BuildPeerCatalogResponses(
        IEnumerable<DadHubRosterCatalogRow> rows,
        DadWorkerSessionId excludeWorkerSessionId)
    {
        var byOwner = new Dictionary<string, DadPeerRosterCatalogResponse>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row == null)
                continue;
            if (!excludeWorkerSessionId.IsEmpty &&
                !row.OwnerWorkerSessionId.IsEmpty &&
                string.Equals(row.OwnerWorkerSessionId.Value, excludeWorkerSessionId.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ownerKey = string.IsNullOrWhiteSpace(row.OwnerWorkerSessionId.Value)
                ? $"client:{row.OwnerClientInstanceId.Trim()}"
                : $"worker:{row.OwnerWorkerSessionId.Value}";
            if (!byOwner.TryGetValue(ownerKey, out var response))
            {
                response = new DadPeerRosterCatalogResponse
                {
                    RequestId = "hub-roster-projection",
                    RespondedAtUtc = DateTime.UtcNow,
                    ClientInstanceId = row.OwnerClientInstanceId,
                    WorkerSessionId = row.OwnerWorkerSessionId,
                    Catalog = new DadAccountRosterCatalog
                    {
                        IsFullRosterAvailable = false,
                        SourceClientInstanceId = row.OwnerClientInstanceId,
                        SourceWorkerSessionId = row.OwnerWorkerSessionId,
                        Summary = "Pushed hub roster projection.",
                        SourceDiagnostics = new DadRosterSourceDiagnostics
                        {
                            LocalAccountKey = row.AccountKey.Value,
                        },
                    },
                };
                byOwner[ownerKey] = response;
            }

            // A compact projection is still an owner catalog. Never let an older client that
            // forwarded another account's retained row turn that row into durable peer knowledge.
            if (!DadRosterKnowledgeSourceRules.IsDeclaredOwnerCatalogRow(response.Catalog, BuildRosterCharacter(row)))
                continue;

            response.Catalog.Characters.Add(BuildRosterCharacter(row));
        }

        return byOwner.Values.ToList();
    }

    private static string BuildRowKey(
        DadWorkerSessionId ownerWorkerSessionId,
        string? ownerClientInstanceId,
        DadAccountKey accountKey,
        DadCharacterKey characterKey,
        ulong contentId)
    {
        var ownerPart = string.IsNullOrWhiteSpace(ownerWorkerSessionId.Value)
            ? $"client:{ownerClientInstanceId?.Trim() ?? string.Empty}"
            : $"worker:{ownerWorkerSessionId.Value.Trim()}";
        return $"{ownerPart}|{DadRosterIdentity.BuildKey(accountKey, characterKey, contentId)}";
    }
}
