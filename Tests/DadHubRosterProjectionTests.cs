using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

// B2: the compact roster projection carried in the hub publish must build from cached catalogs, round-trip
// through the wire serializer intact, and rebuild peer catalog responses on the client side.
public sealed class DadHubRosterProjectionTests
{
    private static DadPeerRosterCatalogResponse BuildResponse(
        string worker,
        string clientInstance,
        params DadRosterCharacter[] characters)
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ClientInstanceId = clientInstance,
            Catalog = new DadAccountRosterCatalog
            {
                SourceWorkerSessionId = new DadWorkerSessionId(worker),
                SourceClientInstanceId = clientInstance,
                Characters = characters.ToList(),
            },
        };

    private static DadRosterCharacter BuildCharacter(string worker, string account, string name, ulong contentId, uint job, int level)
        => new()
        {
            AccountKey = new DadAccountKey(account),
            AccountAlias = account,
            CharacterKey = new DadCharacterKey($"{name}@Behemoth"),
            ContentId = contentId,
            CharacterName = name,
            WorldName = "Behemoth",
            JobLevels = new Dictionary<uint, int> { [job] = level },
            CurrentJobId = job,
            CurrentJobAbbrev = "WAR",
            CurrentLevel = level,
            Source = DadCharacterSource.PeerRuntime,
            SourceWorkerSessionId = new DadWorkerSessionId(worker),
            SourceClientInstanceId = $"{worker}-client",
        };

    [Fact]
    public void BuildCatalogRowsProjectsFieldsAndDeduplicates()
    {
        var character = BuildCharacter("worker-a", "acct-a", "Aaa", 100, 21, 80);
        var response = BuildResponse("worker-a", "worker-a-client", character, character.Clone());

        var rows = DadHubRosterCatalogProjection.BuildCatalogRows(new[] { response });

        var row = Assert.Single(rows);
        Assert.Equal("worker-a", row.OwnerWorkerSessionId.Value);
        Assert.Equal("acct-a", row.AccountKey.Value);
        Assert.Equal(100ul, row.ContentId);
        Assert.Equal(80, row.CurrentLevel);
        Assert.Equal(80, row.JobLevels[21]);
    }

    [Fact]
    public void BuildCatalogRowsRespectsMaxRowCap()
    {
        var responses = Enumerable.Range(0, 10)
            .Select(i => BuildResponse($"worker-{i}", $"client-{i}", BuildCharacter($"worker-{i}", $"acct-{i}", $"C{i}", (ulong)(i + 1), 1, i + 1)))
            .ToList();

        var rows = DadHubRosterCatalogProjection.BuildCatalogRows(responses, maxRows: 4);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void CatalogRowsRoundTripThroughIpcJson()
    {
        var response = BuildResponse(
            "worker-a",
            "worker-a-client",
            BuildCharacter("worker-a", "acct-a", "Aaa", 100, 21, 80),
            BuildCharacter("worker-a", "acct-a", "Bbb", 101, 19, 73));
        var publish = new DadHubRosterPublish
        {
            Generation = 7,
            CatalogRows = DadHubRosterCatalogProjection.BuildCatalogRows(new[] { response }),
        };

        var restored = DadIpcJson.Deserialize<DadHubRosterPublish>(DadIpcJson.Serialize(publish));

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.CatalogRows.Count);
        var first = restored.CatalogRows.Single(r => r.ContentId == 100);
        Assert.Equal("acct-a", first.AccountKey.Value);
        Assert.Equal(80, first.JobLevels[21]);
        Assert.Equal("worker-a", first.OwnerWorkerSessionId.Value);
    }

    [Fact]
    public void BuildPeerCatalogResponsesRebuildsCharactersAndExcludesLocalOwner()
    {
        var rows = DadHubRosterCatalogProjection.BuildCatalogRows(new[]
        {
            BuildResponse("worker-a", "client-a", BuildCharacter("worker-a", "acct-a", "Aaa", 100, 21, 80)),
            BuildResponse("worker-b", "client-b", BuildCharacter("worker-b", "acct-b", "Bbb", 200, 19, 60)),
        });

        var responses = DadHubRosterCatalogProjection.BuildPeerCatalogResponses(rows, new DadWorkerSessionId("worker-b"));

        var response = Assert.Single(responses);
        Assert.Equal("worker-a", response.WorkerSessionId.Value);
        Assert.Equal("acct-a", response.Catalog.SourceDiagnostics.LocalAccountKey);
        var character = Assert.Single(response.Catalog.Characters);
        Assert.Equal(100ul, character.ContentId);
        Assert.Equal(80, character.JobLevels[21]);
        Assert.Equal("worker-a", character.SourceWorkerSessionId.Value);
    }
}
