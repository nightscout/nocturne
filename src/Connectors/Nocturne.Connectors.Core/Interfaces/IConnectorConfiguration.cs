using System;
using Nocturne.Connectors.Core.Models;

#nullable enable

namespace Nocturne.Connectors.Core.Interfaces
{
    /// <summary>
    /// Base interface for all connector configurations
    /// </summary>
    public interface IConnectorConfiguration
    {
        /// <summary>
        /// The data source type
        /// </summary>
        ConnectSource ConnectSource { get; set; }

        /// <summary>
        /// Whether the connector is enabled
        /// </summary>
        bool Enabled { get; set; }

        /// <summary>
        /// Whether to save raw data for debugging
        /// </summary>
        bool SaveRawData { get; set; }

        /// <summary>
        /// Directory for saving/loading data files
        /// </summary>
        string DataDirectory { get; set; }

        /// <summary>
        /// Whether to load data from file instead of API
        /// </summary>
        bool LoadFromFile { get; set; }

        /// <summary>
        /// Specific file path to load from (optional)
        /// </summary>
        string? LoadFilePath { get; set; }

        /// <summary>
        /// Whether to delete data files after successful upload
        /// </summary>
        bool DeleteAfterUpload { get; set; }

        /// <summary>
        /// Whether to use asynchronous message processing
        /// </summary>
        bool UseAsyncProcessing { get; set; }

        /// <summary>
        /// Whether to fallback to direct API if message processing fails
        /// </summary>
        bool FallbackToDirectApi { get; set; }

        /// <summary>
        /// Timeout for message processing
        /// </summary>
        TimeSpan MessageTimeout { get; set; }

        /// <summary>
        /// Maximum retry attempts for failed operations
        /// </summary>
        int MaxRetryAttempts { get; set; }

        /// <summary>
        /// Batch size for processing data
        /// </summary>
        int BatchSize { get; set; }

        /// <summary>
        /// Optional routing key prefix for message bus
        /// </summary>
        string? RoutingKeyPrefix { get; set; }

        /// <summary>
        /// Sync interval in minutes between data synchronization cycles
        /// </summary>
        int SyncIntervalMinutes { get; set; }

        /// <summary>
        /// Operational mode for the connector
        /// </summary>
        ConnectorMode Mode { get; set; }

        /// <summary>
        /// Destination Nightscout URL for uploading data (required for Standalone mode)
        /// </summary>
        string NightscoutUrl { get; set; }

        /// <summary>
        /// Destination Nightscout API secret for authentication (required for Standalone mode)
        /// </summary>
        string NightscoutApiSecret { get; set; }

        /// <summary>
        /// Alternative API secret (for backward compatibility)
        /// </summary>
        string ApiSecret { get; set; }

        /// <summary>
        /// Validates the configuration and throws ArgumentException if invalid
        /// </summary>
        void Validate();
    }
}
