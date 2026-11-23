namespace Nocturne.Core.Models.V4;

/// <summary>
/// The devicestatus sub-objects (configuration, radioAdapter, rileylinks, cgm, xdripjs, etc.) and
/// unknown top-level keys that no typed V4 snapshot holds, kept so the status reads back as it was
/// uploaded.
/// </summary>
/// <remarks>
/// The legacy device status projection and the device status list read it, to rebuild a status's
/// sections. Alert rules do not: the sensor state a status reports is its
/// <see cref="SensorStateReport"/>. Linked to the originating <see cref="DeviceStatus"/> via
/// <see cref="CorrelationId"/>.
/// </remarks>
public class DeviceStatusExtras
{
    /// <summary>
    /// UUID v7 primary key.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant identifier for row-level security.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Links back to the originating DeviceStatus decomposition batch.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Canonical timestamp as UTC DateTime.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Catch-all dictionary of uncaptured sub-objects, stored as JSONB.
    /// </summary>
    public Dictionary<string, object?>? Extras { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }
}
