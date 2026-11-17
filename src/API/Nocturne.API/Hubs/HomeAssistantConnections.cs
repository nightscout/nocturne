namespace Nocturne.API.Hubs;

/// <summary>
/// The live <see cref="HomeAssistantHub"/> connections of each Home Assistant instance, so an alert
/// is marked delivered only when a connection exists to receive it.
/// </summary>
public sealed class HomeAssistantConnections
{
    private readonly Dictionary<(Guid Tenant, string Instance), HashSet<string>> _connections = new();
    private readonly Lock _gate = new();

    /// <summary>Records <paramref name="connectionId"/> as a live connection of the instance.</summary>
    public void Add(Guid tenantId, string instanceId, string connectionId)
    {
        lock (_gate)
        {
            var key = (tenantId, instanceId);
            if (!_connections.TryGetValue(key, out var ids))
                _connections[key] = ids = [];
            ids.Add(connectionId);
        }
    }

    /// <summary>Forgets a connection of the instance that has closed.</summary>
    public void Remove(Guid tenantId, string instanceId, string connectionId)
    {
        lock (_gate)
        {
            var key = (tenantId, instanceId);
            if (_connections.TryGetValue(key, out var ids) && ids.Remove(connectionId) && ids.Count == 0)
                _connections.Remove(key);
        }
    }

    /// <summary>Whether the instance has at least one live connection.</summary>
    public bool IsConnected(Guid tenantId, string instanceId)
    {
        lock (_gate)
            return _connections.ContainsKey((tenantId, instanceId));
    }
}
