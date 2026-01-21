using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;

#nullable enable

namespace Nocturne.Connectors.Core.Models
{
    /// <summary>
    /// Base implementation of connector configuration with common properties
    /// </summary>
    public abstract class BaseConnectorConfiguration : IConnectorConfiguration
    {
        private string _dataDirectory = "./data";
        private string? _contentRootPath;

        [Required]
        public ConnectSource ConnectSource { get; set; }

        [RuntimeConfigurable("Save Raw Data", "Advanced")]
        public bool SaveRawData { get; set; } = false;

        /// <summary>
        /// Whether the connector is enabled and should sync data.
        /// When disabled, the connector enters standby mode.
        /// </summary>
        [RuntimeConfigurable("Enabled", "General")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the data directory path. Can be relative or absolute.
        /// Relative paths are resolved against the content root path.
        /// Default is "data" (resolved to {ContentRootPath}/data).
        /// </summary>
        public string DataDirectory
        {
            get => GetResolvedDataDirectory();
            set => _dataDirectory = value;
        }

        /// <summary>
        /// Gets or sets the content root path used to resolve relative DataDirectory paths.
        /// Set automatically during configuration binding.
        /// </summary>
        public string? ContentRootPath
        {
            get => _contentRootPath;
            set => _contentRootPath = value;
        }

        [RuntimeConfigurable("Load From File", "Advanced")]
        public bool LoadFromFile { get; set; } = false;

        public string? LoadFilePath { get; set; }

        [RuntimeConfigurable("Delete After Upload", "Advanced")]
        public bool DeleteAfterUpload { get; set; } = false;

        public bool UseAsyncProcessing { get; set; } = true;

        public TimeSpan MessageTimeout { get; set; } = TimeSpan.FromMinutes(5);

        [RuntimeConfigurable("Max Retry Attempts", "Advanced")]
        [ConfigSchema(Minimum = 0, Maximum = 10)]
        public int MaxRetryAttempts { get; set; } = 3;

        [RuntimeConfigurable("Batch Size", "Advanced")]
        [ConfigSchema(Minimum = 1, Maximum = 500)]
        public int BatchSize { get; set; } = 50;

        /// <summary>
        /// Timezone offset in hours (default 0).
        /// Can be set via environment variable: CONNECT_{CONNECTORNAME}_TIMEZONE_OFFSET
        /// or appsettings: {Configuration}:TimezoneOffset
        /// </summary>
        [RuntimeConfigurable("Timezone Offset", "General")]
        [ConfigSchema(Minimum = -12, Maximum = 14)]
        public double TimezoneOffset { get; set; } = 0;

        public string? RoutingKeyPrefix { get; set; }

        [RuntimeConfigurable("Sync Interval (Minutes)", "Sync")]
        [ConfigSchema(Minimum = 1, Maximum = 60)]
        public int SyncIntervalMinutes { get; set; } = 5;

        public virtual void Validate()
        {
            if (!Enum.IsDefined(typeof(ConnectSource), ConnectSource))
                throw new ArgumentException($"Invalid connector source: {ConnectSource}");

            if (UseAsyncProcessing)
            {
                if (MessageTimeout <= TimeSpan.Zero)
                    throw new ArgumentException("MessageTimeout must be greater than zero");

                if (MaxRetryAttempts < 0)
                    throw new ArgumentException("MaxRetryAttempts cannot be negative");

                if (BatchSize <= 0)
                    throw new ArgumentException("BatchSize must be greater than zero");

                if (!string.IsNullOrEmpty(RoutingKeyPrefix))
                {
                    if (
                        !System.Text.RegularExpressions.Regex.IsMatch(
                            RoutingKeyPrefix,
                            "^[a-zA-Z0-9.]*$"
                        )
                    )
                        throw new ArgumentException(
                            "RoutingKeyPrefix can only contain alphanumeric characters and dots"
                        );
                }
            }

            ValidateSourceSpecificConfiguration();
        }

        /// <summary>
        /// Override this method to validate connector-specific configuration
        /// </summary>
        protected abstract void ValidateSourceSpecificConfiguration();

        /// <summary>
        /// Resolves the data directory to an absolute path.
        /// If DataDirectory is relative, it is resolved against ContentRootPath.
        /// If ContentRootPath is not set, falls back to AppContext.BaseDirectory.
        /// </summary>
        private string GetResolvedDataDirectory()
        {
            // If already absolute, return as-is
            if (Path.IsPathRooted(_dataDirectory))
            {
                return _dataDirectory;
            }

            if (string.IsNullOrEmpty(_contentRootPath))
            {
                return _dataDirectory;
            }

            // Determine the base path to resolve against
            var basePath = _contentRootPath;

            return Path.GetFullPath(Path.Combine(basePath, _dataDirectory));
        }
    }
}
