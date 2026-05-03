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
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.StateSpans,
        SyncDataType.DeviceEvents
    ]
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
    [ConnectorProperty(ConnectorPropertyKey.Email, Required = true)]
    public string Email { get; init; } = string.Empty;

    /// <summary>
    ///     Glooko account password
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    ///     Glooko server region.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Server,
        DefaultValue = GlookoConstants.RegionUS,
        AllowedValues = [GlookoConstants.RegionCA, GlookoConstants.RegionEU, GlookoConstants.RegionUS])]
    public string Server { get; init; } = GlookoConstants.RegionUS;

    /// <summary>
    ///     Use v3 API for additional data types (alarms, automatic boluses, consumables).
    ///     This provides a single API call instead of multiple v2 calls.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.UseV3Api, DefaultValue = "true")]
    public bool UseV3Api { get; set; } = true;

    /// <summary>
    ///     Include CGM readings from v3 as backup to primary CGM source (e.g., xDrip).
    ///     Only use this if you want Glooko to fill gaps in your primary CGM data.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.V3IncludeCgmBackfill, DefaultValue = "false")]
    public bool V3IncludeCgmBackfill { get; set; } = false;
}
