using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterKnowledgeSourceRulesTests
{
    [Fact]
    public void OrdinaryRefreshRetainsCachedPullAndPushedHubKnowledge()
    {
        var cached = Response("cached", "worker-x", full: true, jobId: 32, level: 95);
        var pushed = Response("pushed", "worker-x", full: false, jobId: 35, level: 100);

        var retained = DadRosterKnowledgeSourceRules.SelectRetainedPeerResponses(
            [cached],
            [pushed],
            new DadWorkerSessionId("worker-local"));

        Assert.Equal(2, retained.Count);
        Assert.Contains(retained, response => response.RequestId == "cached" && response.Catalog.Characters[0].JobLevels[32] == 95);
        Assert.Contains(retained, response => response.RequestId == "pushed" && response.Catalog.Characters[0].JobLevels[35] == 100);
    }

    [Fact]
    public void LocalOwnerProjectionIsNeverRelearnedAsPeerKnowledge()
    {
        var local = Response("local", "worker-local", full: true, jobId: 32, level: 95);
        var peer = Response("peer", "worker-x", full: true, jobId: 35, level: 100);

        var retained = DadRosterKnowledgeSourceRules.SelectRetainedPeerResponses(
            [local, peer],
            [],
            new DadWorkerSessionId("worker-local"));

        Assert.Equal("peer", Assert.Single(retained).RequestId);
    }

    [Fact]
    public void PublishedOwnerLedgerContainsOnlyTheTrueLocalAccount()
    {
        var records = new List<DadRosterKnownCharacterRecord>
        {
            new()
            {
                AccountKey = new DadAccountKey("account-local"),
                CharacterKey = "Local@World",
                ContentId = 100,
                JobLevels = new Dictionary<uint, int> { [32] = 95 },
            },
            new()
            {
                AccountKey = new DadAccountKey("account-peer"),
                CharacterKey = "Peer@World",
                ContentId = 200,
                JobLevels = new Dictionary<uint, int> { [24] = 100 },
            },
        };

        var published = DadRosterKnowledgeSourceRules.SelectTrueLocalOwnerRecords(
            records,
            new DadAccountKey("account-local"));

        var local = Assert.Single(published);
        Assert.Equal("account-local", local.AccountKey.Value);
        Assert.Equal(95, local.JobLevels[32]);
    }

    [Fact]
    public void PublishedOwnerAccountDirectoryContainsOnlyTheTrueLocalAccount()
    {
        var accounts = new List<DadRosterAccountOption>
        {
            new() { AccountKey = new DadAccountKey("account-local"), DisplayName = "Local" },
            new() { AccountKey = new DadAccountKey("account-peer"), DisplayName = "Peer" },
        };

        var published = DadRosterKnowledgeSourceRules.SelectTrueLocalOwnerAccounts(
            accounts,
            new DadAccountKey("account-local"));

        var local = Assert.Single(published);
        Assert.Equal("account-local", local.AccountKey.Value);
    }

    [Fact]
    public void PeerOwnerCatalogCannotContributeARowForAnotherAccount()
    {
        var catalog = new DadAccountRosterCatalog
        {
            IsFullRosterAvailable = true,
            SourceDiagnostics = new DadRosterSourceDiagnostics
            {
                LocalAccountKey = "account-owner",
            },
        };
        var ownerRow = new DadRosterCharacter
        {
            AccountKey = new DadAccountKey("account-owner"),
            CharacterKey = new DadCharacterKey("Owner@World"),
            ContentId = 100,
        };
        var forwardedPeerRow = new DadRosterCharacter
        {
            AccountKey = new DadAccountKey("account-other"),
            CharacterKey = new DadCharacterKey("Other@World"),
            ContentId = 200,
        };

        Assert.True(DadRosterKnowledgeSourceRules.IsDeclaredOwnerCatalogRow(catalog, ownerRow));
        Assert.False(DadRosterKnowledgeSourceRules.IsDeclaredOwnerCatalogRow(catalog, forwardedPeerRow));

        catalog.SourceDiagnostics.LocalAccountKey = string.Empty;
        Assert.False(DadRosterKnowledgeSourceRules.IsDeclaredOwnerCatalogRow(catalog, ownerRow));
    }

    private static DadPeerRosterCatalogResponse Response(
        string requestId,
        string worker,
        bool full,
        uint jobId,
        int level)
        => new()
        {
            RequestId = requestId,
            RespondedAtUtc = new DateTime(2026, 7, 13, 12, requestId == "pushed" ? 1 : 0, 0, DateTimeKind.Utc),
            ClientInstanceId = "client-x",
            WorkerSessionId = new DadWorkerSessionId(worker),
            Catalog = new DadAccountRosterCatalog
            {
                IsFullRosterAvailable = full,
                Characters =
                [
                    new DadRosterCharacter
                    {
                        AccountKey = new DadAccountKey("account-x"),
                        CharacterKey = new DadCharacterKey("Character X@World"),
                        ContentId = 100,
                        JobLevels = new Dictionary<uint, int> { [jobId] = level },
                    },
                ],
            },
        };
}
