using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

#nullable enable

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to TConnectSync connector (Python)
    /// </summary>
    [ConnectorRegistration(
        connectorName: "TConnectSync",
        projectTypeName: "TConnectSync",
        serviceName: ServiceNames.TConnectSyncConnector,
        environmentPrefix: ServiceNames.ConnectorEnvironment.TConnectSyncPrefix,
        connectSourceName: "ConnectSource.TConnectSync",
        dataSourceId: "tconnectsync-connector",
        icon: "tconnect",
        category: ConnectorCategory.Pump,
        description: "Connect to Tandem Source (formerly t:connect)",
        displayName: "Tandem Source",
        type: ConnectorType.PythonApp,
        scriptPath: "../../Connectors/Nocturne.Connectors.TConnectSync"
    )]
    public class TConnectSyncConnectorConfiguration : BaseConnectorConfiguration
    {
        public TConnectSyncConnectorConfiguration()
        {
            ConnectSource = ConnectSource.TConnectSync;
        }

        /// <summary>
        /// Tandem Source email
        /// </summary>
        [Required]
        [EnvironmentVariable("TCONNECT_EMAIL")]
        [AspireParameter("tconnect-email", "Email", secret: true, description: "Tandem Source email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Tandem Source password
        /// </summary>
        [Required]
        [EnvironmentVariable("TCONNECT_PASSWORD")]
        [AspireParameter("tconnect-password", "Password", secret: true, description: "Tandem Source password")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Tandem server region (US or EU)
        /// </summary>
        [EnvironmentVariable("TCONNECT_REGION")]
        [AspireParameter("tconnect-region", "Region", secret: false, description: "Tandem Source region (US or EU)", defaultValue: "US")]
        public string Region { get; set; } = "US";

        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(Email))
                throw new ArgumentException(
                    "TCONNECT_EMAIL is required (tconnect-email) for TConnectSync connector"
                );

            if (string.IsNullOrWhiteSpace(Password))
                throw new ArgumentException(
                    "TCONNECT_PASSWORD is required (tconnect-password) for TConnectSync connector"
                );
        }
    }
}
