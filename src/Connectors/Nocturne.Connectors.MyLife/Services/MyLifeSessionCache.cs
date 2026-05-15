using System.Collections.Concurrent;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeSessionCache : IMyLifeSessionCache
{
    private readonly ConcurrentDictionary<Guid, MyLifeSession> _sessions = new();

    public MyLifeSession? Get(Guid tenantId)
    {
        _sessions.TryGetValue(tenantId, out var session);
        return session;
    }

    public void Set(Guid tenantId, MyLifeSession session)
    {
        _sessions[tenantId] = session;
    }

    public void Invalidate(string connectorName, Guid tenantId)
    {
        if (connectorName.Equals("MyLife", StringComparison.OrdinalIgnoreCase))
            _sessions.TryRemove(tenantId, out _);
    }
}
