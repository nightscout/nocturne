using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Eversense.Configurations;

[ConnectorRegistration(
    "Eversense",
    ServiceNames.EversenseConnector,
    "EVERSENSE",
    "ConnectSource.Eversense",
    "eversense-connector",
    "eversense",
    ConnectorCategory.Cgm,
    "Connect to Eversense Now",
    "Eversense",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = EversenseConnectorConfiguration.MaxHistoryDays,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose],
    SensorReadingIntervalSeconds = 300
)]
public class EversenseConnectorConfiguration : BaseConnectorConfiguration
{
    public EversenseConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Eversense;
    }

    /// <summary>
    /// Eversense Now account username (email).
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Eversense Now account password.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// How far back a sync may ask the care API for glucose history. The Eversense apps upload up
    /// to 30 days after a gap; how much the API returns has not been confirmed.
    /// </summary>
    public const int MaxHistoryDays = 30;

    /// <summary>
    /// Server region: "US", or "OUS" for accounts outside the United States.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Server, DefaultValue = "US", AllowedValues = ["US", "OUS"])]
    public string Server { get; init; } = "US";

    /// <summary>
    /// Username (email) of the patient to follow. Required when the account follows multiple patients.
    /// If omitted and only one patient is followed, that patient is auto-selected.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PatientUsername)]
    public string? PatientUsername { get; init; }
}
