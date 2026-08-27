using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadFrenRiderInboundQueueRulesTests
{
    [Fact]
    public void ProfileNotRequiredRetainsExistingQueueBehavior()
    {
        Assert.True(DadFrenRiderInboundQueueRules.IsAllowed(
            useFrenRider: false,
            outcome: null,
            frenRiderLoaded: false,
            out var blocker));
        Assert.Empty(blocker);
    }

    [Theory]
    [InlineData((int)DadFrenRiderProfileApplicationOutcome.TemporaryApplied)]
    [InlineData((int)DadFrenRiderProfileApplicationOutcome.PermanentApplied)]
    public void AppliedProfilesRequireFrenRiderToRemainLoaded(
        int outcomeValue)
    {
        var outcome = (DadFrenRiderProfileApplicationOutcome)outcomeValue;
        var applied = new DadFrenRiderProfileApplicationResult(true, outcome, "applied");

        Assert.False(DadFrenRiderInboundQueueRules.IsAllowed(true, applied, false, out var blocker));
        Assert.Equal("dad-inbound-frenrider-unavailable", blocker);
        Assert.True(DadFrenRiderInboundQueueRules.IsAllowed(true, applied, true, out blocker));
        Assert.Equal("applied", blocker);
    }

    [Fact]
    public void OptedOutAllowsQueueWithoutFrenRiderMutationOrAvailability()
    {
        var optedOut = new DadFrenRiderProfileApplicationResult(
            true,
            DadFrenRiderProfileApplicationOutcome.OptedOut,
            "dad-frenrider-opted-out");

        Assert.True(DadFrenRiderInboundQueueRules.IsAllowed(true, optedOut, false, out var blocker));
        Assert.Equal("dad-frenrider-opted-out", blocker);
    }

    [Fact]
    public void MissingInvalidAndFailedOutcomesDenyQueue()
    {
        Assert.False(DadFrenRiderInboundQueueRules.IsAllowed(true, null, true, out var missing));
        Assert.Equal("dad-inbound-frenrider-profile-not-applied", missing);

        var failed = DadFrenRiderProfileApplicationResult.Failed("profile-invalid");
        Assert.False(DadFrenRiderInboundQueueRules.IsAllowed(true, failed, true, out var invalid));
        Assert.Equal("profile-invalid", invalid);
    }

    [Fact]
    public void FormApplicationRemainsAfterAuthoritativePartyProof()
    {
        var source = ReadRepositorySource("Plugin.cs");
        var form = Slice(source, "private ValueTask<DadAutoPartyExecutionResult> ExecuteInboundAutoPartyForm", "private bool TryValidateInboundFrenRiderProfile");

        var followerProof = form.IndexOf("followerObservedContentIds.Length", StringComparison.Ordinal);
        var followerApply = form.IndexOf("TryApplyInboundFrenRiderProfile", followerProof, StringComparison.Ordinal);
        Assert.True(followerProof >= 0 && followerApply > followerProof);

        var slotOneProof = form.IndexOf("result.AuthoritativePartyMembers", StringComparison.Ordinal);
        var slotOneApply = form.IndexOf("TryApplyInboundFrenRiderProfile", slotOneProof, StringComparison.Ordinal);
        Assert.True(slotOneProof >= 0 && slotOneApply > slotOneProof);
        Assert.Contains("completedInboundAutoPartyForms.Add", form, StringComparison.Ordinal);
        Assert.DoesNotContain("IntegrationProfileReceipt", form, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionOperationReceipt", form, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalExpiryReleasesOnlyTheExactTemporaryProfileOwnership()
    {
        var source = ReadRepositorySource("Plugin.cs");
        var cleanup = Slice(
            source,
            "private void ReleaseExpiredInboundFrenRiderProfile",
            "private static DadFrenRiderProfileOwnership BuildFrenRiderProfileOwnership");

        Assert.Contains("target.ProposalId", cleanup, StringComparison.Ordinal);
        Assert.Contains("target.SenderIslandId", cleanup, StringComparison.Ordinal);
        Assert.Contains("target.OwnerId", cleanup, StringComparison.Ordinal);
        Assert.Contains("target.OpaqueCharacterId", cleanup, StringComparison.Ordinal);
        Assert.Contains("DadFrenRiderProfileApplicationOutcome.TemporaryApplied", cleanup, StringComparison.Ordinal);
        Assert.Contains("FrenRiderProfileTransferService.ReleaseTemporary(ownership", cleanup, StringComparison.Ordinal);
        Assert.Contains("inboundFrenRiderProfileOutcomes.Remove(ownership)", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseAllInboundFrenRiderProfiles", cleanup, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dad.csproj")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the DAD repository root.");
        return File.ReadAllText(Path.Combine([root, .. pathParts]));
    }
}
