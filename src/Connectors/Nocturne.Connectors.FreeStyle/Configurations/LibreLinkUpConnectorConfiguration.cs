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
    SupportedDataTypes = [SyncDataType.Glucose],
    SensorReadingIntervalSeconds = 300
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
    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp password
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp region
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Region, DefaultValue = "EU", AllowedValues = ["EU", "US", "AE", "AP", "AU", "CA", "DE", "FR", "JP"])]
    public string Region { get; set; } = "EU";

    /// <summary>
    ///     Patient ID for LibreLinkUp (for caregiver accounts)
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PatientId)]
    public string PatientId { get; set; } = string.Empty;
}
