using System.Collections.Concurrent;
using System.Net;

namespace CrabSenseKiosk.Services;

// A registration supplies only a candidate address. It is never an authority
// for device identity: the worker must verify the signed response before use.
public sealed class ControllerEndpointRegistry
{
    private readonly ConcurrentDictionary<string, (string Ip, DateTime Seen)> _candidates =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string code, IPAddress? address)
    {
        if (address is null || string.IsNullOrWhiteSpace(code)) return;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!IsPrivate(address.ToString())) return;
        _candidates[code] = (address.ToString(), DateTime.UtcNow);
    }

    public string? Candidate(string code) =>
        _candidates.TryGetValue(code, out var entry) && entry.Seen > DateTime.UtcNow.AddMinutes(-2)
            ? entry.Ip : null;

    public static bool IsPrivate(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || ip.ToString() != value) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
