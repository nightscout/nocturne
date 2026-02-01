using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.FreeStyle.Configurations;

/// <summary>
///     Configuration specific to LibreLinkUp connector
/// </summary>
[ConnectorRegistration(
    "LibreLinkUp",
    "Nocturne_Connectors_FreeStyle",
    ServiceNames.LibreConnector,
    ServiceNames.ConnectorEnvironment.FreeStylePrefix,
    "ConnectSource.LibreLinkUp",
    "libre-connector",
    "libre",
    ConnectorCategory.Cgm,
    "Connect to LibreView for CGM data",
    "FreeStyle Libre"
)]
public class LibreLinkUpConnectorConfiguration : BaseConnectorConfiguration
{
    public LibreLinkUpConnectorConfiguration()
    {
        ConnectSource = ConnectSource.LibreLinkUp;
    }

    /// <summary>
    ///     LibreLinkUp username
    /// </summary>
    [Required]
    [AspireParameter(
        "librelinkup-username",
        "Username",
        true,
        "LibreLinkUp account username"
    )]
    [EnvironmentVariable("CONNECT_LIBRE_USERNAME")]
    [RuntimeConfigurable("Username", "Connection")]
    public string LibreUsername { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp password
    /// </summary>
    [Required]
    [Secret]
    [AspireParameter(
        "librelinkup-password",
        "Password",
        true,
        "LibreLinkUp account password"
    )]
    [EnvironmentVariable("CONNECT_LIBRE_PASSWORD")]
    public string LibrePassword { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp region
    /// </summary>
    [AspireParameter(
        "librelinkup-region",
        "Region",
        false,
        "LibreLinkUp region (EU, US, etc.)",
        "EU"
    )]
    [EnvironmentVariable("CONNECT_LIBRE_REGION")]
    [RuntimeConfigurable("Region", "Connection")]
    [ConfigSchema(Enum = new[] { "EU", "US", "AE", "AP", "AU", "CA", "DE", "FR", "JP" })]
    public string LibreRegion { get; set; } = "EU";

    /// <summary>
    ///     Patient ID for LibreLinkUp (for caregiver accounts)
    /// </summary>
    [AspireParameter(
        "librelinkup-patient-id",
        "PatientId",
        false,
        "Patient ID for caregiver accounts",
        ""
    )]
    [EnvironmentVariable("CONNECT_LIBRE_PATIENT_ID")]
    [RuntimeConfigurable("Patient ID", "Connection")]
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