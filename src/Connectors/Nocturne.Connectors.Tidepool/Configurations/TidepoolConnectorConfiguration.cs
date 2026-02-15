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
    SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.Treatments]
)]
public class TidepoolConnectorConfiguration : BaseConnectorConfiguration
{
    public TidepoolConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Tidepool;
    }

    [ConnectorProperty("Username",
        Required = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Tidepool account email address")]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty("Password",
        Required = true,
        Secret = true,
        Description = "Tidepool account password")]
    public string Password { get; set; } = string.Empty;

    [ConnectorProperty("Server",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Tidepool server region",
        DefaultValue = "US",
        AllowedValues = ["US", "Development"])]
    public string Server { get; set; } = "US";

    [ConnectorProperty("UserId",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Tidepool user ID (leave empty to use the authenticated user)")]
    public string UserId { get; set; } = string.Empty;
}
