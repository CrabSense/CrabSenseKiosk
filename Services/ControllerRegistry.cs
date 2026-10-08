using System.Collections.Concurrent;

namespace CrabSenseKiosk.Services;

public interface IControllerRegistry
{
    void Set(string deviceCode, string edgeState);
    bool IsAccepted(string deviceCode);
    bool MarkIgnored(string deviceCode);
    bool IsBlocked(string deviceCode);
}

public sealed class ControllerRegistry : IControllerRegistry
{
    private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string deviceCode, string edgeState)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
            return;
        _states[deviceCode.Trim()] = edgeState;
        _ignored.TryRemove(deviceCode.Trim(), out _);
    }

    public bool IsAccepted(string deviceCode) =>
        _states.TryGetValue(deviceCode, out var state)
        && state is "Approved" or "Online";

    public bool MarkIgnored(string deviceCode) =>
        !string.IsNullOrWhiteSpace(deviceCode) && _ignored.TryAdd(deviceCode.Trim(), 0);

    public bool IsBlocked(string deviceCode) =>
        _states.TryGetValue(deviceCode, out var state)
        && state is "Rejected" or "Revoked";
}
