using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;

#nullable enable
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to Tidepool connector
    /// </summary>
    [ConnectorRegistration(
        connectorName: "Tidepool",
        projectTypeName: "Nocturne_Connectors_Tidepool",
        serviceName: ServiceNames.TidepoolConnector,
        environmentPrefix: ServiceNames.ConnectorEnvironment.TidepoolPrefix,
        connectSourceName: "ConnectSource.Tidepool",
        dataSourceId: "tidepool-connector",
        icon: "tidepool",
        category: ConnectorCategory.Cgm,
        description: "Connect to Tidepool to sync CGM, pump, and treatment data",
        displayName: "Tidepool"
    )]
    public class TidepoolConnectorConfiguration : BaseConnectorConfiguration
    {
        public TidepoolConnectorConfiguration()
        {
            ConnectSource = ConnectSource.Tidepool;
        }

        /// <summary>
        /// Tidepool account email/username
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_TIDEPOOL_USERNAME")]
        [AspireParameter("tidepool-username", "Username", secret: false, description: "Tidepool account email")]
        public string TidepoolUsername { get; set; } = string.Empty;

        /// <summary>
        /// Tidepool account password
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_TIDEPOOL_PASSWORD")]
        [AspireParameter("tidepool-password", "Password", secret: true, description: "Tidepool account password")]
        public string TidepoolPassword { get; set; } = string.Empty;

        /// <summary>
        /// Tidepool API server URL
        /// </summary>
        [EnvironmentVariable("CONNECT_TIDEPOOL_SERVER")]
        [AspireParameter("tidepool-server", "Server", secret: false, description: "Tidepool API server URL", defaultValue: "https://api.tidepool.org")]
        public string TidepoolServer { get; set; } = "https://api.tidepool.org";

        /// <summary>
        /// Whether to sync treatment data (bolus, carbs, exercise)
        /// </summary>
        [EnvironmentVariable("CONNECT_TIDEPOOL_SYNC_TREATMENTS")]
        [AspireParameter("tidepool-sync-treatments", "SyncTreatments", secret: false, description: "Sync treatment data (bolus, carbs, exercise)", defaultValue: "true")]
        public bool SyncTreatments { get; set; } = true;

        /// <summary>
        /// Whether to sync profile data (basal schedules, ISF, ICR, targets)
        /// </summary>
        [EnvironmentVariable("CONNECT_TIDEPOOL_SYNC_PROFILES")]
        [AspireParameter("tidepool-sync-profiles", "SyncProfiles", secret: false, description: "Sync profile data (basal, ISF, ICR, targets)", defaultValue: "true")]
        public bool SyncProfiles { get; set; } = true;

        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(TidepoolUsername))
                throw new ArgumentException(
                    "CONNECT_TIDEPOOL_USERNAME is required when using Tidepool source"
                );

            if (string.IsNullOrWhiteSpace(TidepoolPassword))
                throw new ArgumentException(
                    "CONNECT_TIDEPOOL_PASSWORD is required when using Tidepool source"
                );

            if (string.IsNullOrWhiteSpace(TidepoolServer))
                throw new ArgumentException(
                    "CONNECT_TIDEPOOL_SERVER must be a valid URL"
                );
        }
    }
}
