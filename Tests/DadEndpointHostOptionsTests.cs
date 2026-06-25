using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadEndpointHostOptionsTests
{
    [Fact]
    public void BuildOptionsAlwaysPlacesLoopbackFirst()
    {
        var options = DadEndpointHostOptions.BuildOptions(
        [
            new DadEndpointHostCandidate("192.168.1.12", "Ethernet"),
        ]);

        Assert.Equal("127.0.0.1", options[0].Host);
        Assert.Contains("loopback", options[0].Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOptionsAddsDeduplicatedLanIpv4AddressesInInputOrder()
    {
        var options = DadEndpointHostOptions.BuildOptions(
        [
            new DadEndpointHostCandidate("192.168.1.12", "Ethernet"),
            new DadEndpointHostCandidate("192.168.1.12", "Wi-Fi"),
            new DadEndpointHostCandidate("10.0.0.5", "VPN"),
        ]);

        Assert.Equal(["127.0.0.1", "192.168.1.12", "10.0.0.5"], options.Select(static option => option.Host).ToArray());
        Assert.Contains("Ethernet", options[1].Label, StringComparison.Ordinal);
        Assert.Contains("VPN", options[2].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOptionsIgnoresLoopbackIpv4Ipv6AndInvalidHostsFromCandidates()
    {
        var options = DadEndpointHostOptions.BuildOptions(
        [
            new DadEndpointHostCandidate("127.0.0.2", "Loopback"),
            new DadEndpointHostCandidate("::1", "IPv6 loopback"),
            new DadEndpointHostCandidate("fe80::1", "IPv6"),
            new DadEndpointHostCandidate("not-a-host", "Invalid"),
            new DadEndpointHostCandidate("172.16.20.30", "LAN"),
        ]);

        Assert.Equal(["127.0.0.1", "172.16.20.30"], options.Select(static option => option.Host).ToArray());
    }
}
