using System;
using System.IO;
using DotNetEnv;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Core.Services
{
    /// <summary>
    /// Base configuration service for loading common connector configuration
    /// NOTE: This service no longer creates specific configurations.
    /// Each connector project should implement its own configuration factory and service.
    /// </summary>
    public abstract class BaseConfigurationService
    {
        /// <summary>
        /// Load configuration for the specific connector
        /// Override this method in derived classes to create the appropriate configuration type
        /// </summary>
        public abstract IConnectorConfiguration LoadConfiguration();

        /// <summary>
        /// Load environment variables from .env file if it exists
        /// </summary>
        protected static void LoadEnvironmentFile()
        {
            if (File.Exists(".env"))
            {
                Env.Load(".env");
            }
        }

        /// <summary>
        /// Parse the CONNECT_SOURCE environment variable to ConnectSource enum
        /// </summary>
        protected static ConnectSource GetConnectSourceFromEnvironment()
        {
            var connectSource =
                Environment.GetEnvironmentVariable("CONNECT_SOURCE") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connectSource))
            {
                throw new InvalidOperationException(
                    "CONNECT_SOURCE environment variable is required"
                );
            }

            return ConnectorConfigurationFactory.ParseConnectSource(connectSource);
        }

        /// <summary>
        /// Populate base configuration properties that are common to all connectors
        /// </summary>
        protected static void PopulateBaseConfiguration(IConnectorConfiguration config)
        {
            // File I/O Configuration
            config.SaveRawData =
                bool.TryParse(
                    Environment.GetEnvironmentVariable("CONNECT_SAVE_RAW_DATA"),
                    out var saveRaw
                ) && saveRaw;

            config.DataDirectory =
                Environment.GetEnvironmentVariable("CONNECT_DATA_DIRECTORY") ?? "./data";

            config.LoadFromFile =
                bool.TryParse(
                    Environment.GetEnvironmentVariable(ConnectorEnvironmentVariables.LoadFromFile),
                    out var loadFromFile
                ) && loadFromFile;

            config.LoadFilePath = Environment.GetEnvironmentVariable("CONNECT_LOAD_FILE_PATH");

            config.DeleteAfterUpload =
                bool.TryParse(
                    Environment.GetEnvironmentVariable("CONNECT_DELETE_AFTER_UPLOAD"),
                    out var deleteAfter
                ) && deleteAfter;

            // Message Bus Configuration
            config.UseAsyncProcessing = bool.TryParse(
                Environment.GetEnvironmentVariable("CONNECT_USE_ASYNC_PROCESSING"),
                out var useAsync
            )
                ? useAsync
                : true;


            config.MaxRetryAttempts = int.TryParse(
                Environment.GetEnvironmentVariable("CONNECT_MAX_RETRY_ATTEMPTS"),
                out var maxRetries
            )
                ? maxRetries
                : 3;

            config.BatchSize = int.TryParse(
                Environment.GetEnvironmentVariable("CONNECT_BATCH_SIZE"),
                out var batchSize
            )
                ? batchSize
                : 50;

            config.RoutingKeyPrefix = Environment.GetEnvironmentVariable(
                "CONNECT_ROUTING_KEY_PREFIX"
            );

            var messageTimeoutMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("CONNECT_MESSAGE_TIMEOUT_MINUTES"),
                out var timeoutMinutes
            )
                ? timeoutMinutes
                : 5;
            config.MessageTimeout = TimeSpan.FromMinutes(messageTimeoutMinutes);
        }

        /// <summary>
        /// Validate the configuration
        /// </summary>
        protected static void ValidateConfiguration(IConnectorConfiguration config)
        {
            config.Validate();
        }
    }
}
