using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;

#nullable enable
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Configurations
{
    /// <summary>
    /// Configuration specific to MiniMed CareLink connector
    /// </summary>
    [ConnectorRegistration(
        connectorName: "MiniMed",
        projectTypeName: "Nocturne_Connectors_MiniMed",
        serviceName: ServiceNames.MiniMedConnector,
        environmentPrefix: ServiceNames.ConnectorEnvironment.MiniMedPrefix,
        connectSourceName: "ConnectSource.CareLink",
        dataSourceId: "minimed-connector",
        icon: "medtronic",
        category: ConnectorCategory.Pump,
        description: "Sync data from MiniMed pumps",
        displayName: "Medtronic CareLink"
    )]
    public class CareLinkConnectorConfiguration : BaseConnectorConfiguration
    {
        public CareLinkConnectorConfiguration()
        {
            ConnectSource = ConnectSource.CareLink;
        }

        /// <summary>
        /// CareLink username
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_CARE_LINK_USERNAME")]
        [AspireParameter("carelink-username", "Username", secret: false, description: "CareLink account username")]
        public string CareLinkUsername { get; set; } = string.Empty;

        /// <summary>
        /// CareLink password
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_CARE_LINK_PASSWORD")]
        [AspireParameter("carelink-password", "Password", secret: true, description: "CareLink account password")]
        public string CareLinkPassword { get; set; } = string.Empty;

        /// <summary>
        /// CareLink country code (e.g., US, GB)
        /// </summary>
        [Required]
        [EnvironmentVariable("CONNECT_CARE_LINK_COUNTRY")]
        [AspireParameter("carelink-country", "CountryCode", secret: false, description: "CareLink country code (e.g., US, GB)", defaultValue: "US")]
        public string CareLinkCountry { get; set; } = "US";

        /// <summary>
        /// Patient username (if different from account username)
        /// </summary>
        [EnvironmentVariable("CONNECT_CARE_LINK_PATIENT_USERNAME")]
        [AspireParameter("carelink-patient-username", "PatientUsername", secret: false, description: "Patient username (if different from account)", defaultValue: "")]
        public string CareLinkPatientUsername { get; set; } = string.Empty;

        protected override void ValidateSourceSpecificConfiguration()
        {
            if (string.IsNullOrWhiteSpace(CareLinkUsername))
                throw new ArgumentException(
                    "CONNECT_CARE_LINK_USERNAME is required when using MiniMed CareLink source"
                );

            if (string.IsNullOrWhiteSpace(CareLinkPassword))
                throw new ArgumentException(
                    "CONNECT_CARE_LINK_PASSWORD is required when using MiniMed CareLink source"
                );
        }
    }
}
