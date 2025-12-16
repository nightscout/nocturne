using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;

#nullable enable
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to Glooko connector
    /// </summary>
    [ConnectorRegistration(
        connectorName: "Glooko",
        projectTypeName: "Nocturne_Connectors_Glooko",
        serviceName: ServiceNames.GlookoConnector,
        environmentPrefix: ServiceNames.ConnectorEnvironment.GlookoPrefix,
        connectSourceName: "ConnectSource.Glooko",
        dataSourceId: "glooko-connector",
        icon: "glooko",
        category: ConnectorCategory.Sync,
        description: "Import data from Glooko platform",
        displayName: "Glooko"
    )]
    public class GlookoConnectorConfiguration : BaseConnectorConfiguration
    {
        public GlookoConnectorConfiguration()
        {
            ConnectSource = ConnectSource.Glooko;
        }

        /// <summary>
        /// Glooko account email
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_GLOOKO_USERNAME")]
        [AspireParameter(
            "glooko-username",
            "Email",
            secret: false,
            description: "Glooko account email"
        )]
        public string GlookoUsername { get; set; } = string.Empty;

        /// <summary>
        /// Glooko account password
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_GLOOKO_PASSWORD")]
        [AspireParameter(
            "glooko-password",
            "Password",
            secret: true,
            description: "Glooko account password"
        )]
        public string GlookoPassword { get; set; } = string.Empty;

        /// <summary>
        /// Glooko server region (US or EU)
        /// </summary>
        [EnvironmentVariable("CONNECT_GLOOKO_SERVER")]
        [AspireParameter(
            "glooko-server",
            "Server",
            secret: false,
            description: "Glooko server (US or EU)",
            defaultValue: "US"
        )]
        public string GlookoServer { get; set; } = "US";



        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(GlookoUsername))
                throw new ArgumentException(
                    "CONNECT_GLOOKO_USERNAME is required when using Glooko source"
                );

            if (string.IsNullOrWhiteSpace(GlookoPassword))
                throw new ArgumentException(
                    "CONNECT_GLOOKO_PASSWORD is required when using Glooko source"
                );
        }
    }
}
