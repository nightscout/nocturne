using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;

#nullable enable

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to Dexcom Share connector
    /// </summary>
    [ConnectorRegistration(
        connectorName: "Dexcom",
        projectTypeName: "Nocturne_Connectors_Dexcom",
        serviceName: "ServiceNames.DexcomConnector",
        environmentPrefix: "ServiceNames.ConnectorEnvironment.DexcomPrefix",
        connectSourceName: "ConnectSource.Dexcom",
        dataSourceId: "dexcom-connector",
        icon: "dexcom",
        category: ConnectorCategory.Cgm,
        description: "Connect to Dexcom Share or Clarity",
        displayName: "Dexcom"
    )]
    public class DexcomConnectorConfiguration : BaseConnectorConfiguration
    {
        public DexcomConnectorConfiguration()
        {
            ConnectSource = ConnectSource.Dexcom;
        }

        /// <summary>
        /// Dexcom Share username
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_DEXCOM_USERNAME")]
        [AspireParameter("dexcom-username", "Username", secret: false, description: "Dexcom account username")]
        public string DexcomUsername { get; set; } = string.Empty;

        /// <summary>
        /// Dexcom Share password
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_DEXCOM_PASSWORD")]
        [AspireParameter("dexcom-password", "Password", secret: true, description: "Dexcom account password")]
        public string DexcomPassword { get; set; } = string.Empty;

        /// <summary>
        /// Dexcom server region (US or EU)
        /// </summary>
        [EnvironmentVariable("CONNECT_DEXCOM_SERVER")]
        [AspireParameter("dexcom-server", "Server", secret: false, description: "Dexcom server (US or EU)", defaultValue: "US")]
        public string DexcomServer { get; set; } = "US";

        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(DexcomUsername))
                throw new ArgumentException(
                    "CONNECT_DEXCOM_USERNAME is required when using Dexcom Share source"
                );

            if (string.IsNullOrWhiteSpace(DexcomPassword))
                throw new ArgumentException(
                    "CONNECT_DEXCOM_PASSWORD is required when using Dexcom Share source"
                );
        }
    }
}
