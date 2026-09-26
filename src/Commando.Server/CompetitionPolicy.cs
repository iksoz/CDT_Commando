using System.Net;
using System.Text.Json;
using Commando.Shared;

namespace Commando.Server;

public sealed class CompetitionPolicy
{
    public DateTimeOffset StartsUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public bool RequireSourceIp { get; set; } = true;
    public List<string> AllowedNetworks { get; set; } = [];
    public List<CompetitionHost> Hosts { get; set; } = [];

    public static CompetitionPolicy Load(string? configuredPath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Set COMMANDO_POLICY_FILE to an explicit competition policy JSON file.");
        }

        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRoot, configuredPath);
        var policy = JsonSerializer.Deserialize<CompetitionPolicy>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Competition policy is empty.");
        policy.Validate();
        return policy;
    }

    public CompetitionHost? MatchRegistration(AgentRegistrationRequest request, IPAddress? remoteAddress)
    {
        var address = remoteAddress?.MapToIPv4();
        return Hosts.SingleOrDefault(host =>
            host.Hostname.Equals(request.Hostname, StringComparison.OrdinalIgnoreCase) &&
            host.OsFamily.Equals(request.OsFamily, StringComparison.OrdinalIgnoreCase) &&
            (!RequireSourceIp || IPAddress.TryParse(host.Address, out var expected) && expected.Equals(address)));
    }

    public CompetitionHost? FindHost(string policyName) =>
        Hosts.SingleOrDefault(host => host.Name.Equals(policyName, StringComparison.OrdinalIgnoreCase));

    public bool IsAllowedTarget(string target) =>
        IPAddress.TryParse(target, out var address) &&
        Hosts.Any(host => IPAddress.TryParse(host.Address, out var expected) && expected.Equals(address));

    public string CurrentState(bool paused, bool emergencyStopped, DateTimeOffset now)
    {
        if (emergencyStopped) return "emergency_stopped";
        if (now >= ExpiresUtc) return "ended";
        if (now < StartsUtc) return "pre_competition";
        return paused ? "paused" : "live";
    }

    private void Validate()
    {
        if (StartsUtc == default || ExpiresUtc <= StartsUtc)
            throw new InvalidOperationException("Competition policy requires valid startsUtc and expiresUtc values.");
        if (AllowedNetworks.Count == 0 || Hosts.Count == 0)
            throw new InvalidOperationException("Competition policy requires explicit networks and hosts.");
        if (AllowedNetworks.Any(network => !ScopeNetwork.TryParse(network, out _)))
            throw new InvalidOperationException("Competition policy contains an invalid allowed network.");

        var networks = AllowedNetworks.Select(network => ScopeNetwork.Parse(network)).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in Hosts)
        {
            if (string.IsNullOrWhiteSpace(host.Name) || string.IsNullOrWhiteSpace(host.Hostname) ||
                !IPAddress.TryParse(host.Address, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                !networks.Any(network => network.Contains(address)) ||
                host.OsFamily is not ("Windows" or "Linux") ||
                host.AllowUnrestrictedPowerShell && host.OsFamily != "Windows" ||
                !names.Add(host.Name) || !hostnames.Add(host.Hostname) || !addresses.Add(host.Address) ||
                host.AllowedServiceIds.Any(id => string.IsNullOrWhiteSpace(id)) ||
                host.AllowedServiceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != host.AllowedServiceIds.Count)
            {
                throw new InvalidOperationException($"Invalid or duplicate competition host '{host.Name}'.");
            }
        }
    }
}

public sealed class CompetitionHost
{
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string OsFamily { get; set; } = string.Empty;
    public List<string> AllowedServiceIds { get; set; } = [];
    public bool AllowUnrestrictedPowerShell { get; set; }
}

public readonly record struct ScopeNetwork(IPAddress Address, int PrefixLength)
{
    public static ScopeNetwork Parse(string value)
    {
        if (!TryParse(value, out var result)) throw new FormatException("Invalid CIDR network.");
        return result;
    }

    public static bool TryParse(string value, out ScopeNetwork result)
    {
        result = default;
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32) return false;
        result = new ScopeNetwork(address, prefix);
        return true;
    }

    public bool Contains(IPAddress candidate)
    {
        if (candidate.AddressFamily != Address.AddressFamily) return false;
        var expected = Address.GetAddressBytes();
        var actual = candidate.GetAddressBytes();
        var whole = PrefixLength / 8;
        for (var i = 0; i < whole; i++) if (expected[i] != actual[i]) return false;
        var bits = PrefixLength % 8;
        return bits == 0 || (expected[whole] & (0xff << (8 - bits))) == (actual[whole] & (0xff << (8 - bits)));
    }
}
