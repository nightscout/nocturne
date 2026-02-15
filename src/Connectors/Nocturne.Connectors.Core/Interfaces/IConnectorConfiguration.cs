using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Base interface for all connector configurations
/// </summary>
public interface IConnectorConfiguration
{
    /// <summary>
    ///     The data source type
    /// </summary>
    ConnectSource ConnectSource { get; set; }

    /// <summary>
    ///     Whether the connector is enabled
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    ///     Maximum retry attempts for failed operations
    /// </summary>
    int MaxRetryAttempts { get; set; }

    /// <summary>
    ///     Batch size for processing data
    /// </summary>
    int BatchSize { get; set; }

    int SyncIntervalMinutes { get; set; }

    /// <summary>
    ///     Validates the configuration and throws ArgumentException if invalid
    /// </summary>
    void Validate();
}