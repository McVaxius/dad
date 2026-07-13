using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadRunCancellationLedgerTests
{
    [Fact]
    public void CancellationTombstonePermanentlyRejectsDelayedMutationForThatRun()
    {
        var ledger = new DadRunCancellationLedger();

        Assert.True(ledger.CanAccept("run-1"));
        Assert.True(ledger.Record("run-1"));
        Assert.False(ledger.CanAccept("run-1"));
        Assert.True(ledger.CanAccept("run-2"));
        Assert.False(ledger.Record("run-1"));
        Assert.False(ledger.CanAccept("run-1"));
    }
}
