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
    SupportsHistoricalSync = false,
    MaxHistoricalDays = 0,
    SupportsManualSync = false,
    SupportedDataTypes = [SyncDataType.Glucose]
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
    /// Server region. Currently only "US" is supported.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Server, DefaultValue = "US", AllowedValues = ["US"])]
    public string Server { get; init; } = "US";

    /// <summary>
    /// Username (email) of the patient to follow. Required when the account follows multiple patients.
    /// If omitted and only one patient is followed, that patient is auto-selected.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PatientUsername)]
    public string? PatientUsername { get; init; }
}
