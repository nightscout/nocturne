using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Glooko.Configurations;

/// <summary>
///     Configuration specific to Glooko connector
/// </summary>
[ConnectorRegistration(
    "Glooko",
    ServiceNames.GlookoConnector,
    "GLOOKO",
    "ConnectSource.Glooko",
    "glooko-connector",
    "glooko",
    ConnectorCategory.Sync,
    "Import data from Glooko platform",
    "Glooko",
    SupportsHistoricalSync = true,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.Treatments]
)]
public class GlookoConnectorConfiguration : BaseConnectorConfiguration
{
    public GlookoConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Glooko;
    }

    /// <summary>
    ///     Glooko account email
    /// </summary>
    [ConnectorProperty("Email",
        Required = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Glooko account email")]
    public string Email { get; init; } = string.Empty;

    /// <summary>
    ///     Glooko account password
    /// </summary>
    [ConnectorProperty("Password",
        Required = true,
        Secret = true,
        Description = "Glooko account password")]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    ///     Glooko server region (US or EU)
    /// </summary>
    [ConnectorProperty("Server",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Glooko server (US or EU)",
        DefaultValue = "US",
        AllowedValues = ["US", "EU"])]
    public string Server { get; init; } = "US";

    /// <summary>
    ///     Use v3 API for additional data types (alarms, automatic boluses, consumables).
    ///     This provides a single API call instead of multiple v2 calls.
    /// </summary>
    [ConnectorProperty("UseV3Api",
        RuntimeConfigurable = true,
        Category = "Advanced",
        Description = "Use V3 API for additional data types",
        DefaultValue = "true")]
    public bool UseV3Api { get; set; } = true;

    /// <summary>
    ///     Include CGM readings from v3 as backup to primary CGM source (e.g., xDrip).
    ///     Only use this if you want Glooko to fill gaps in your primary CGM data.
    /// </summary>
    [ConnectorProperty("V3IncludeCgmBackfill",
        RuntimeConfigurable = true,
        Category = "Advanced",
        Description = "Include CGM readings as backup",
        DefaultValue = "false")]
    public bool V3IncludeCgmBackfill { get; set; } = false;
}
