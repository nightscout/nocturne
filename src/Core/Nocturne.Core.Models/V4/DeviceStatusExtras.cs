namespace Nocturne.Core.Models.V4;

/// <summary>
/// Captures uncaptured devicestatus sub-objects (configuration, radioAdapter, rileylinks, xdripjs, etc.)
/// that are not decomposed into dedicated V4 snapshot tables.
/// </summary>
/// <remarks>
/// Alpha-phase diagnostic data that may be dropped in future releases.
/// Linked to the originating <see cref="DeviceStatus"/> via <see cref="CorrelationId"/>.
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

    /// <summary>
    /// When the record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the record was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; set; }
}
