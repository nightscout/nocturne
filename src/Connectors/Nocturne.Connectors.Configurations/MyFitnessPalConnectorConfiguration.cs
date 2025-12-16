using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;

#nullable enable
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to MyFitnessPal connector
    /// </summary>
    [ConnectorRegistration(
        connectorName: "MyFitnessPal",
        projectTypeName: "Nocturne_Connectors_MyFitnessPal",
        serviceName: ServiceNames.MyFitnessPalConnector,
        environmentPrefix: ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix,
        connectSourceName: "ConnectSource.MyFitnessPal",
        dataSourceId: "myfitnesspal-connector",
        icon: "myfitnesspal",
        category: ConnectorCategory.Nutrition,
        description: "Import meals and nutrition data",
        displayName: "MyFitnessPal"
    )]
    public class MyFitnessPalConnectorConfiguration : BaseConnectorConfiguration
    {
        public MyFitnessPalConnectorConfiguration()
        {
            ConnectSource = ConnectSource.MyFitnessPal;
        }

        /// <summary>
        /// MyFitnessPal username/email
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_MFP_USERNAME")]
        [AspireParameter("mfp-username", "Username", secret: false, description: "MyFitnessPal username")]
        public string MyFitnessPalUsername { get; set; } = string.Empty;

        /// <summary>
        /// MyFitnessPal password
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_MFP_PASSWORD")]
        [AspireParameter("mfp-password", "Password", secret: true, description: "MyFitnessPal password")]
        public string MyFitnessPalPassword { get; set; } = string.Empty;

        /// <summary>
        /// MyFitnessPal API key (if available)
        /// </summary>
        [EnvironmentVariable("CONNECT_MFP_API_KEY")]
        [AspireParameter("mfp-api-key", "ApiKey", secret: true, description: "MyFitnessPal API Key (optional)", defaultValue: "")]
        public string MyFitnessPalApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Number of days to sync from MyFitnessPal (defaults to 7)
        /// </summary>
        [AspireParameter("mfp-sync-days", "SyncDays", secret: false, description: "Number of days to sync", defaultValue: "7")]
        public int SyncDays { get; set; } = 7;

        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(MyFitnessPalUsername))
                throw new ArgumentException(
                    "CONNECT_MYFITNESSPAL_USERNAME is required when using MyFitnessPal source"
                );

            if (string.IsNullOrWhiteSpace(MyFitnessPalPassword))
                throw new ArgumentException(
                    "CONNECT_MYFITNESSPAL_PASSWORD is required when using MyFitnessPal source"
                );
        }
    }
}
