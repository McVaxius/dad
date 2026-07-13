using dad.Models;

namespace dad.Services;

internal static class DadRosterKnowledgeSourceRules
{
    public static bool IsTrueLocalOwnerAccount(DadAccountKey candidate, DadAccountKey localOwner)
        => !candidate.IsEmpty &&
           !localOwner.IsEmpty &&
           DadRosterIdentity.SameAccount(candidate, localOwner);

    public static IReadOnlyList<DadRosterKnownCharacterRecord> SelectTrueLocalOwnerRecords(
        IEnumerable<DadRosterKnownCharacterRecord> records,
        DadAccountKey localOwner)
        => records
            .Where(record => record != null && IsTrueLocalOwnerAccount(record.AccountKey, localOwner))
            .ToList();

    public static IReadOnlyList<DadRosterAccountOption> SelectTrueLocalOwnerAccounts(
        IEnumerable<DadRosterAccountOption> accounts,
        DadAccountKey localOwner)
        => accounts
            .Where(account => account != null && IsTrueLocalOwnerAccount(account.AccountKey, localOwner))
            .ToList();

    public static bool IsDeclaredOwnerCatalogRow(
        DadAccountRosterCatalog catalog,
        DadRosterCharacter character)
        => catalog != null &&
           character != null &&
           IsTrueLocalOwnerAccount(
               character.AccountKey,
               new DadAccountKey(catalog.SourceDiagnostics?.LocalAccountKey ?? string.Empty));

    public static IReadOnlyList<DadPeerRosterCatalogResponse> SelectRetainedPeerResponses(
        IEnumerable<DadPeerRosterCatalogResponse>? cachedPullResponses,
        IEnumerable<DadPeerRosterCatalogResponse>? pushedHubResponses,
        DadWorkerSessionId localWorkerSessionId)
        => (cachedPullResponses ?? [])
            .Concat(pushedHubResponses ?? [])
            .Where(response => response != null)
            .Where(response => !string.Equals(
                response.WorkerSessionId.Value,
                localWorkerSessionId.Value,
                StringComparison.OrdinalIgnoreCase))
            .DistinctBy(BuildCacheKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static response => response.RespondedAtUtc)
            .ToList();

    public static string BuildCacheKey(DadPeerRosterCatalogResponse response)
        => string.Join(
            '|',
            response.WorkerSessionId.Value?.Trim() ?? string.Empty,
            response.ClientInstanceId?.Trim() ?? string.Empty,
            response.RequestId?.Trim() ?? string.Empty,
            response.RespondedAtUtc.Ticks,
            response.Catalog.IsFullRosterAvailable,
            response.Catalog.Characters.Count);
}
