using dad.Models;
using Xunit;

namespace dad.Tests;

public sealed class DadAuthorityViewStateTests
{
    [Fact]
    public void ConfiguredEndpointWithoutRoutableSessionIsNotRemoteAuthority()
    {
        var transport = new DadPeerTransportSnapshot
        {
            AuthorityEndpoint = "192.168.0.94:4647",
            AuthorityWorkerSessionId = new DadWorkerSessionId(string.Empty),
            AuthorityRoutable = false,
        };

        var view = DadAuthorityViewBuilder.Build(
            DadRunResult.Idle(),
            DadRunResult.Idle(),
            transport,
            new DadWorkerSessionId("client-x"),
            localOnlyModeEnabled: false,
            lastSuccessfulRefreshUtc: null,
            utcNow: DateTime.UtcNow,
            staleThreshold: TimeSpan.FromSeconds(4));

        Assert.Equal(DadAuthorityViewKind.NoRemoteAuthority, view.Kind);
        Assert.False(view.HasRemoteAuthority);
        Assert.Equal("192.168.0.94:4647", view.AuthorityEndpointText);
    }

    [Fact]
    public void HandshakenWorkerIsRemoteAuthority()
    {
        var worker = new DadWorkerSessionId("coordinator-w");
        var transport = new DadPeerTransportSnapshot
        {
            AuthorityEndpoint = "192.168.0.94:4647",
            AuthorityWorkerSessionId = worker,
            AuthorityRoutable = true,
        };
        var authority = DadRunResult.Idle();
        authority.AuthorityWorkerSessionId = worker;
        authority.AuthorityEndpoint = transport.AuthorityEndpoint;

        var now = DateTime.UtcNow;
        var view = DadAuthorityViewBuilder.Build(
            DadRunResult.Idle(),
            authority,
            transport,
            new DadWorkerSessionId("client-x"),
            localOnlyModeEnabled: false,
            lastSuccessfulRefreshUtc: now,
            utcNow: now,
            staleThreshold: TimeSpan.FromSeconds(4));

        Assert.Equal(DadAuthorityViewKind.RemoteIdle, view.Kind);
        Assert.True(view.HasRemoteAuthority);
        Assert.True(view.IsFresh);
    }
}
