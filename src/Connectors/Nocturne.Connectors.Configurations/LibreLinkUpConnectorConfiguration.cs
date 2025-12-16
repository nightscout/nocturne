using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;

#nullable enable
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to LibreLinkUp connector
    /// </summary>
    [ConnectorRegistration(
        connectorName: "LibreLinkUp",
        projectTypeName: "Nocturne_Connectors_FreeStyle",
        serviceName: ServiceNames.LibreConnector,
        environmentPrefix: ServiceNames.ConnectorEnvironment.FreeStylePrefix,
        connectSourceName: "ConnectSource.LibreLinkUp",
        dataSourceId: "libre-connector",
        icon: "libre",
        category: ConnectorCategory.Cgm,
        description: "Connect to LibreView for CGM data",
        displayName: "FreeStyle Libre"
    )]
    public class LibreLinkUpConnectorConfiguration : BaseConnectorConfiguration
    {
        public LibreLinkUpConnectorConfiguration()
        {
            ConnectSource = ConnectSource.LibreLinkUp;
        }

        /// <summary>
        /// LibreLinkUp username
        /// </summary>
        [Required]
        [AspireParameter(
            "librelinkup-username",
            "Username",
            secret: true,
            description: "LibreLinkUp account username"
        )]
        [EnvironmentVariable("CONNECT_LIBRE_USERNAME")]
        public string LibreUsername { get; set; } = string.Empty;

        /// <summary>
        /// LibreLinkUp password
        /// </summary>
        [Required]
        [AspireParameter(
            "librelinkup-password",
            "Password",
            secret: true,
            description: "LibreLinkUp account password"
        )]
        [EnvironmentVariable("CONNECT_LIBRE_PASSWORD")]
        public string LibrePassword { get; set; } = string.Empty;

        /// <summary>
        /// LibreLinkUp region
        /// </summary>
        [AspireParameter(
            "librelinkup-region",
            "Region",
            secret: false,
            description: "LibreLinkUp region (EU, US, etc.)",
            defaultValue: "EU"
        )]
        [EnvironmentVariable("CONNECT_LIBRE_REGION")]
        public string LibreRegion { get; set; } = "EU";

        /// <summary>
        /// LibreLinkUp server URL (optional)
        /// </summary>
        [AspireParameter(
            "librelinkup-server",
            "Server",
            secret: false,
            description: "Custom server URL (optional)",
            defaultValue: ""
        )]
        public string LibreServer { get; set; } = string.Empty;

        /// <summary>
        /// Patient ID for LibreLinkUp (for caregiver accounts)
        /// </summary>
        [AspireParameter(
            "librelinkup-patient-id",
            "PatientId",
            secret: false,
            description: "Patient ID for caregiver accounts",
            defaultValue: ""
        )]
        [EnvironmentVariable("CONNECT_LIBRE_PATIENT_ID")]
        public string LibrePatientId { get; set; } = string.Empty;

        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(LibreUsername))
                throw new ArgumentException(
                    "CONNECT_LINK_UP_USERNAME is required when using LibreLinkUp source"
                );

            if (string.IsNullOrWhiteSpace(LibrePassword))
                throw new ArgumentException(
                    "CONNECT_LINK_UP_PASSWORD is required when using LibreLinkUp source"
                );
        }
    }
}
