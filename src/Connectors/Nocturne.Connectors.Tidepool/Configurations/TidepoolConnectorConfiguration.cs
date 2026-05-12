using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Tidepool.Configurations;

[ConnectorRegistration(
    "Tidepool",
    ServiceNames.TidepoolConnector,
    "TIDEPOOL",
    "ConnectSource.Tidepool",
    "tidepool-connector",
    "tidepool",
    ConnectorCategory.Sync,
    "Sync glucose, treatment, and profile data from Tidepool",
    "Tidepool",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 365,
    SupportsManualSync = true,
    DefaultActiveThresholdMinutes = 180,
    DefaultStaleThresholdMinutes = 360,
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.Activity
    ]
)]
public class TidepoolConnectorConfiguration : BaseConnectorConfiguration
{
    public TidepoolConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Tidepool;
    }

    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.Server, DefaultValue = "US", AllowedValues = ["US", "Development"])]
    public string Server { get; set; } = "US";

    [ConnectorProperty(ConnectorPropertyKey.UserId)]
    public string UserId { get; set; } = string.Empty;
}
