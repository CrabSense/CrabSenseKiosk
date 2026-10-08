using System.Collections.Concurrent;

namespace CrabSenseKiosk.Services;

public interface IControllerRegistry
{
    void Set(string deviceCode, string edgeState);
    bool IsBlocked(string deviceCode);
}

public sealed class ControllerRegistry : IControllerRegistry
{
    private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string deviceCode, string edgeState)
    {
        if (!string.IsNullOrWhiteSpace(deviceCode))
            _states[deviceCode.Trim()] = edgeState;
    }

    public bool IsBlocked(string deviceCode) =>
        _states.TryGetValue(deviceCode, out var state)
        && state is "Rejected" or "Revoked";
}
