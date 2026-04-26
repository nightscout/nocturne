using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models.V4;

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
    ///     How the connector's glucose readings were processed by the source system.
    /// </summary>
    GlucoseProcessing GlucoseProcessing { get; set; }

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

    /// <summary>
    ///     Checks whether a specific data type is enabled for syncing
    /// </summary>
    bool IsDataTypeEnabled(SyncDataType type);

    /// <summary>
    ///     Filters a list of supported data types to only those enabled in configuration
    /// </summary>
    List<SyncDataType> GetEnabledDataTypes(List<SyncDataType> supportedTypes);
}