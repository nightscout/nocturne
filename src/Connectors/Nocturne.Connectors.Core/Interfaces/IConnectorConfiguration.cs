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

        int SyncIntervalMinutes { get; set; }

        /// <summary>
        /// Validates the configuration and throws ArgumentException if invalid
        /// </summary>
        void Validate();
    }
}
