using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.FreeStyle.Configurations;

/// <summary>
///     Configuration specific to LibreLinkUp connector
/// </summary>
[ConnectorRegistration(
    "LibreLinkUp",
    ServiceNames.LibreConnector,
    "LIBRE",
    "ConnectSource.LibreLinkUp",
    "libre-connector",
    "libre",
    ConnectorCategory.Cgm,
    "Connect to LibreView for CGM data",
    "FreeStyle Libre",
    SupportsHistoricalSync = false,
    MaxHistoricalDays = 7,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose]
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
    [ConnectorProperty("Username",
        Required = true,
        Secret = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "LibreLinkUp account username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp password
    /// </summary>
    [ConnectorProperty("Password",
        Required = true,
        Secret = true,
        Description = "LibreLinkUp account password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp region
    /// </summary>
    [ConnectorProperty("Region",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "LibreLinkUp region (EU, US, etc.)",
        DefaultValue = "EU",
        AllowedValues = ["EU", "US", "AE", "AP", "AU", "CA", "DE", "FR", "JP"])]
    public string Region { get; set; } = "EU";

    /// <summary>
    ///     Patient ID for LibreLinkUp (for caregiver accounts)
    /// </summary>
    [ConnectorProperty("PatientId",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Patient ID for caregiver accounts")]
    public string PatientId { get; set; } = string.Empty;
}
