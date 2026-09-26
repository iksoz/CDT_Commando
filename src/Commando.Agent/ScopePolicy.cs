using System.Net;

namespace Commando.Agent;

public sealed class ScopePolicy(IEnumerable<string> allowedTargets, IEnumerable<string> allowedCidrs)
{
    private readonly HashSet<string> _allowedTargets = new(
        allowedTargets.Select(NormalizeTarget),
        StringComparer.OrdinalIgnoreCase);
    private readonly List<(IPAddress Network, int PrefixLength)> _allowedNetworks =
        allowedCidrs.Select(ParseCidr).ToList();

    public bool IsTargetAllowed(string target)
    {
        var normalized = NormalizeTarget(target);
        if (_allowedTargets.Contains(normalized))
        {
            return true;
        }

        return IPAddress.TryParse(normalized, out var address) &&
               _allowedNetworks.Any(network => Contains(network.Network, network.PrefixLength, address));
    }

    public static bool IsValidCidr(string cidr)
    {
        try
        {
            _ = ParseCidr(cidr);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static (IPAddress Network, int PrefixLength) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            throw new FormatException("CIDR must contain an IP address and prefix length.");
        }

        var bitLength = address.GetAddressBytes().Length * 8;
        if (prefixLength < 0 || prefixLength > bitLength)
        {
            throw new FormatException("CIDR prefix length is outside the address range.");
        }

        return (address, prefixLength);
    }

    private static bool Contains(IPAddress network, int prefixLength, IPAddress candidate)
    {
        var networkBytes = network.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        if (networkBytes.Length != candidateBytes.Length)
        {
            return false;
        }

        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (networkBytes[index] != candidateBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (networkBytes[wholeBytes] & mask) == (candidateBytes[wholeBytes] & mask);
    }

    private static string NormalizeTarget(string target) => target.Trim().TrimEnd('.');
}
