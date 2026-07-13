using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadRosterKnowledgeLearningCursorTests
{
    [Fact]
    public void NewLocalPoolObservationAdvancesWithoutPeerCacheActivity()
    {
        var cursor = new DadRosterKnowledgeLearningCursor();
        var firstPool = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(cursor.TryAdvance(7, firstPool));
        Assert.False(cursor.TryAdvance(7, firstPool));
        Assert.True(cursor.TryAdvance(7, firstPool.AddSeconds(5)));
        Assert.False(cursor.TryAdvance(7, firstPool.AddSeconds(5)));
    }

    [Fact]
    public void PeerCatalogRevisionStillAdvancesWhenPoolTimestampIsUnchanged()
    {
        var cursor = new DadRosterKnowledgeLearningCursor();
        var poolTime = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(cursor.TryAdvance(1, poolTime));
        Assert.True(cursor.TryAdvance(2, poolTime));
        Assert.False(cursor.TryAdvance(2, poolTime.AddTicks(-1)));
    }
}
