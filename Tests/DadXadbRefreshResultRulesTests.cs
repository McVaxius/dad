using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadXadbRefreshResultRulesTests
{
    [Fact]
    public void SummaryWarningsDoNotEraseSuccessfulRefreshAndSaveEvidence()
    {
        var status = new DadXadbStatus
        {
            IsReady = true,
            LastRefreshUtc = DateTime.UtcNow,
            LastSaveUtc = DateTime.UtcNow,
            Warnings = ["XADB summary JSON unreadable."],
        };

        Assert.True(DadXadbRefreshResultRules.MutationSucceeded(status, saveAfterRefresh: true));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void MutationRequiresReadyAndExactActionCompletion(
        bool ready,
        bool refreshed,
        bool saved)
    {
        var status = new DadXadbStatus
        {
            IsReady = ready,
            LastRefreshUtc = refreshed ? DateTime.UtcNow : null,
            LastSaveUtc = saved ? DateTime.UtcNow : null,
        };

        Assert.Equal(
            ready && refreshed && saved,
            DadXadbRefreshResultRules.MutationSucceeded(status, saveAfterRefresh: true));
        Assert.Equal(
            ready && refreshed,
            DadXadbRefreshResultRules.MutationSucceeded(status, saveAfterRefresh: false));
    }
}
