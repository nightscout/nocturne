using System;
using System.Linq;
using Nocturne.Connectors.Core.Models;

#nullable enable

namespace Nocturne.Connectors.Core.Services
{
    /// <summary>
    /// Factory utilities for connector sources
    /// NOTE: This factory no longer creates configurations directly.
    /// Each connector project should implement its own configuration factory.
    /// </summary>
    public static class ConnectorConfigurationFactory
    {
        /// <summary>
        /// Parse a string to ConnectSource enum
        /// </summary>
        /// <param name="connectSource">The connector source string</param>
        /// <returns>Parsed ConnectSource enum value</returns>
        public static ConnectSource ParseConnectSource(string connectSource)
        {
            if (string.IsNullOrWhiteSpace(connectSource))
                throw new ArgumentException(
                    "Connect source cannot be null or empty",
                    nameof(connectSource)
                );

            var source = connectSource.ToLowerInvariant();

            return source switch
            {
                "dexcomshare" or "dexcom" => ConnectSource.Dexcom,
                "glooko" => ConnectSource.Glooko,
                "minimedcarelink" or "carelink" => ConnectSource.CareLink,
                "linkup" or "librelinkup" => ConnectSource.LibreLinkUp,
                "nightscout" => ConnectSource.Nightscout,
                "myfitnesspal" => ConnectSource.MyFitnessPal,
                "mylife" => ConnectSource.MyLife,
                _ => throw new ArgumentException(
                    $"Unknown CONNECT_SOURCE: {connectSource}. Supported sources: {string.Join(", ", GetSupportedSources())}"
                ),
            };
        }

        /// <summary>
        /// Get all supported connector sources
        /// </summary>
        /// <returns>Array of supported connector source strings</returns>
        public static string[] GetSupportedSources()
        {
            return new[]
            {
                "dexcom",
                "dexcomshare",
                "glooko",
                "carelink",
                "minimedcarelink",
                "librelinkup",
                "linkup",
                "nightscout",
                "myfitnesspal",
                "mylife",
            };
        }

        /// <summary>
        /// Get all available ConnectSource enum values
        /// </summary>
        /// <returns>Array of ConnectSource enum values</returns>
        public static ConnectSource[] GetAvailableSources()
        {
            return Enum.GetValues<ConnectSource>();
        }

        /// <summary>
        /// Validate that a connector source is supported
        /// </summary>
        /// <param name="source">The connector source to validate</param>
        /// <returns>True if the source is supported, false otherwise</returns>
        public static bool IsValidSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;

            try
            {
                ParseConnectSource(source);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
