using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadAdsConfigurationReadinessTests
{
    [Fact]
    public void ResponsivePatchIpcIsReadinessProofEvenWhenMetadataLags()
    {
        Assert.True(DadAdsConfigurationPatchRules.TryEvaluateReadiness(
            installedMetadataReportsLoaded: false,
            responseJson: "{\"success\":true}",
            invocationFailure: null,
            out var reason), reason);
    }

    [Fact]
    public void UnloadedAdsEndpointIsAttributedAsUnloaded()
    {
        Assert.False(DadAdsConfigurationPatchRules.TryEvaluateReadiness(
            installedMetadataReportsLoaded: false,
            responseJson: null,
            invocationFailure: "IPC endpoint has no provider",
            out var reason));
        Assert.Contains("unloaded", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADS.PatchConfigurationJson", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedStaleBuildWithoutPatchEndpointIsAttributedAsMissingOrStale()
    {
        Assert.False(DadAdsConfigurationPatchRules.TryEvaluateReadiness(
            installedMetadataReportsLoaded: true,
            responseJson: null,
            invocationFailure: "IPC endpoint has no provider",
            out var reason));
        Assert.Contains("missing or stale", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADS.PatchConfigurationJson", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveEndpointRejectionIsNotReadiness()
    {
        Assert.False(DadAdsConfigurationPatchRules.TryEvaluateReadiness(
            installedMetadataReportsLoaded: true,
            responseJson: "{\"success\":false,\"message\":\"Invalid lootMode\"}",
            invocationFailure: null,
            out var reason));
        Assert.Contains("rejected", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invalid lootMode", reason, StringComparison.Ordinal);
    }
}
