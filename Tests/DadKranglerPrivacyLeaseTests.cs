using System.Text.Json;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadKranglerPrivacyLeaseTests
{
    [Fact]
    public void AcquireUsesOneProcessTokenAndDoesNotPollAfterOwnership()
    {
        var endpoint = new FakeLeaseEndpoint();
        using var controller = endpoint.CreateController("process-token");

        controller.SetDesired(true);
        controller.Update();
        controller.Update();

        Assert.True(controller.Snapshot.OwnedByThisProcess);
        Assert.Equal(1, endpoint.StatusCalls);
        Assert.Equal(1, endpoint.AcquireCalls);
        Assert.Equal("process-token", endpoint.OwnerToken);
        Assert.All(endpoint.ObservedTokens, token => Assert.Equal("process-token", token));
    }

    [Fact]
    public void ReconcileReacquiresAfterPluginStateChangeAndReplacesStaleOwner()
    {
        var endpoint = new FakeLeaseEndpoint { OwnerToken = "stale-dad-process" };
        using var controller = endpoint.CreateController("current-dad-process");

        controller.SetDesired(true);
        controller.Update();

        Assert.True(controller.Snapshot.OwnedByThisProcess);
        Assert.Equal("current-dad-process", endpoint.OwnerToken);
        Assert.Equal(1, endpoint.AcquireCalls);

        endpoint.OwnerToken = null;
        controller.RequestReconcile();
        controller.Update();

        Assert.Equal("current-dad-process", endpoint.OwnerToken);
        Assert.Equal(2, endpoint.StatusCalls);
        Assert.Equal(2, endpoint.AcquireCalls);
    }

    [Fact]
    public void ReconcileRetainsExactOwnershipWithoutAnotherAcquire()
    {
        var endpoint = new FakeLeaseEndpoint();
        using var controller = endpoint.CreateController("same-token");
        controller.SetDesired(true);
        controller.Update();

        controller.RequestReconcile();
        controller.Update();

        Assert.Equal(2, endpoint.StatusCalls);
        Assert.Equal(1, endpoint.AcquireCalls);
        Assert.True(controller.Snapshot.OwnedByThisProcess);
    }

    [Fact]
    public void DisableAndDisposeReleaseOnlyTheSameProcessTokenOnce()
    {
        var endpoint = new FakeLeaseEndpoint();
        var controller = endpoint.CreateController("exact-owner");
        controller.SetDesired(true);
        controller.Update();

        controller.SetDesired(false);
        controller.SetDesired(false);

        Assert.Null(endpoint.OwnerToken);
        Assert.Equal(1, endpoint.ReleaseCalls);
        Assert.Equal("exact-owner", endpoint.LastReleasedToken);

        controller.Dispose();
        Assert.Equal(1, endpoint.ReleaseCalls);
    }

    [Fact]
    public void IneffectiveLeaseResponseFailsClosed()
    {
        var endpoint = new FakeLeaseEndpoint { IncludeSelf = false };
        using var controller = endpoint.CreateController("process-token");

        controller.SetDesired(true);
        controller.Update();

        Assert.False(controller.Snapshot.OwnedByThisProcess);
        Assert.Equal("process-token", endpoint.OwnerToken);
        Assert.Contains("acquire", controller.Snapshot.SafeCode, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeLeaseEndpoint
    {
        public string? OwnerToken { get; set; }
        public bool IncludeSelf { get; set; } = true;
        public int StatusCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public string LastReleasedToken { get; private set; } = string.Empty;
        public List<string> ObservedTokens { get; } = [];

        public DadKranglerPrivacyLeaseController CreateController(string token)
            => new(Acquire, Release, Status, processToken: token);

        private string Status(string requestJson)
        {
            StatusCalls++;
            var token = ReadToken(requestJson);
            ObservedTokens.Add(token);
            var owned = string.Equals(OwnerToken, token, StringComparison.Ordinal);
            return Response(
                true,
                OwnerToken == null ? "not-held" : owned ? "owned" : "owned-by-other",
                OwnerToken != null,
                owned);
        }

        private string Acquire(string requestJson)
        {
            AcquireCalls++;
            var token = ReadToken(requestJson);
            ObservedTokens.Add(token);
            OwnerToken = token;
            return Response(true, "acquired", true, true);
        }

        private string Release(string requestJson)
        {
            ReleaseCalls++;
            var token = ReadToken(requestJson);
            ObservedTokens.Add(token);
            LastReleasedToken = token;
            var owned = string.Equals(OwnerToken, token, StringComparison.Ordinal);
            if (owned)
                OwnerToken = null;
            return Response(owned, owned ? "released" : "not-owner", !owned && OwnerToken != null, false);
        }

        private string Response(bool ok, string code, bool active, bool owned)
            => JsonSerializer.Serialize(new
            {
                ok,
                code,
                leaseActive = active,
                ownedByRequester = owned,
                namePrivacyActive = active,
                chatPrivacyActive = active,
                includesSelf = active && IncludeSelf,
                status = code,
                error = ok ? string.Empty : code,
            });

        private static string ReadToken(string requestJson)
        {
            using var document = JsonDocument.Parse(requestJson);
            return document.RootElement.GetProperty("token").GetString() ?? string.Empty;
        }
    }
}
