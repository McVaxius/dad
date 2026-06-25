using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace dad.Services;

public sealed record DadEndpointHostCandidate(string Host, string InterfaceName);

public sealed record DadEndpointHostOption(string Host, string Label);

public static class DadEndpointHostOptions
{
    public static IReadOnlyList<DadEndpointHostOption> GetLocalIpv4Options()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(static networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static networkInterface => networkInterface
                .GetIPProperties()
                .UnicastAddresses
                .Where(static address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new DadEndpointHostCandidate(
                    address.Address.ToString(),
                    networkInterface.Name)));

        return BuildOptions(candidates);
    }

    public static IReadOnlyList<DadEndpointHostOption> BuildOptions(
        IEnumerable<DadEndpointHostCandidate> candidates)
    {
        var options = new List<DadEndpointHostOption>
        {
            new("127.0.0.1", "127.0.0.1 (loopback)"),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1",
        };

        foreach (var candidate in candidates)
        {
            if (!TryNormalizeLanIpv4(candidate.Host, out var host) ||
                !seen.Add(host))
            {
                continue;
            }

            var interfaceName = candidate.InterfaceName?.Trim() ?? string.Empty;
            var label = string.IsNullOrWhiteSpace(interfaceName)
                ? host
                : $"{host} ({interfaceName})";
            options.Add(new DadEndpointHostOption(host, label));
        }

        return options;
    }

    private static bool TryNormalizeLanIpv4(string host, out string normalized)
    {
        normalized = string.Empty;
        if (!IPAddress.TryParse(host?.Trim(), out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }
}
