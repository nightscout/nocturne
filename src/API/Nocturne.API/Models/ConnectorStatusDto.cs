using System.Collections.Generic;

namespace Nocturne.API.Models;

/// <summary>
/// DTO representing the current operational status of a data source connector
/// (e.g. Dexcom, Glooko, Libre). Returned by the admin connector status endpoints.
/// </summary>
public class ConnectorStatusDto
{
    /// <summary>
    /// Unique identifier of the connector configuration.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Human-readable connector name (e.g. "Dexcom Share", "LibreLink").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// High-level status label (e.g. "Active", "Error", "Disabled").
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// Total number of entries imported by this connector over its lifetime.
    /// </summary>
    public long TotalEntries { get; set; }

    /// <summary>
    /// Timestamp of the most recent entry imported by this connector.
    /// </summary>
    public DateTime? LastEntryTime { get; set; }

    /// <summary>
    /// Number of entries imported in the last 24 hours.
    /// </summary>
    public int EntriesLast24Hours { get; set; }

    /// <summary>
    /// Current operational state of the connector.
    /// </summary>
    /// <value>Defaults to "Idle".</value>
    public string State { get; set; } = "Idle";

    /// <summary>
    /// Optional message providing detail about the current state (e.g. error description).
    /// </summary>
    public string? StateMessage { get; set; }

    /// <summary>
    /// Whether the connector is considered healthy based on recent sync activity.
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// When the connector last attempted to sync
    /// </summary>
    public DateTime? LastSyncAttempt { get; set; }

    /// <summary>
    /// When the connector last successfully completed a sync
    /// </summary>
    public DateTime? LastSuccessfulSync { get; set; }

    /// <summary>
    /// When the last error occurred
    /// </summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>
    /// Whether the connector is enabled in configuration.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Whether a configuration exists in the database.
    /// </summary>
    public bool HasDatabaseConfig { get; set; }

    /// <summary>
    /// Whether the connector has secrets configured.
    /// </summary>
    public bool HasSecrets { get; set; }

    /// <summary>
    /// Breakdown of total items processed by data type
    /// Keys are data type names (e.g., "Glucose", "Treatments", "Food")
    /// </summary>
    public Dictionary<string, long>? TotalItemsBreakdown { get; set; }

    /// <summary>
    /// Breakdown of items processed in the last 24 hours by data type
    /// Keys are data type names (e.g., "Glucose", "Treatments", "Food")
    /// </summary>
    public Dictionary<string, int>? ItemsLast24HoursBreakdown { get; set; }
}
