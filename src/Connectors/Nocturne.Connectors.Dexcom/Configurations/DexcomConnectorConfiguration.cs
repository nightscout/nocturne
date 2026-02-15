using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Dexcom.Configurations;

/// <summary>
///     Configuration specific to Dexcom Share connector
/// </summary>
[ConnectorRegistration(
    "Dexcom",
    ServiceNames.DexcomConnector,
    "DEXCOM",
    "ConnectSource.Dexcom",
    "dexcom-connector",
    "dexcom",
    ConnectorCategory.Cgm,
    "Connect to Dexcom Share or Clarity",
    "Dexcom",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 90,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose]
)]
public class DexcomConnectorConfiguration : BaseConnectorConfiguration
{
    public DexcomConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Dexcom;
    }

    /// <summary>
    ///     Dexcom Share username
    /// </summary>
    [ConnectorProperty("Username",
        Required = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Dexcom account username")]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    ///     Dexcom Share password
    /// </summary>
    [ConnectorProperty("Password",
        Required = true,
        Secret = true,
        Description = "Dexcom account password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    ///     Dexcom server region (US or EU)
    /// </summary>
    [ConnectorProperty("Server",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Dexcom server (US or EU)",
        DefaultValue = "US",
        AllowedValues = ["US", "EU"])]
    public string Server { get; init; } = "US";
}
