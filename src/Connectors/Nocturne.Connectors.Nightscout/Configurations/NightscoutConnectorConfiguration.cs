using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Nightscout.Configurations;

[ConnectorRegistration(
    "Nightscout",
    ServiceNames.NightscoutConnector,
    "NIGHTSCOUT",
    "ConnectSource.Nightscout",
    "nightscout-connector",
    "nightscout",
    ConnectorCategory.Sync,
    "Sync glucose, treatment, device status, and profile data from a Nightscout instance",
    "Nightscout",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 365,
    SupportsManualSync = true,
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.Notes,
        SyncDataType.DeviceEvents,
        SyncDataType.Profiles,
        SyncDataType.DeviceStatus,
        SyncDataType.Food,
        SyncDataType.Activity
    ]
)]
public class NightscoutConnectorConfiguration : BaseConnectorConfiguration
{
    public NightscoutConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Nightscout;
    }

    [ConnectorProperty(ConnectorPropertyKey.Url, Required = true, Format = "uri")]
    public string Url { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.RealtimeUrl, Format = "uri")]
    public string RealtimeUrl { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.ApiSecret, Required = true, Secret = true)]
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>The largest page the connector asks the source for.</summary>
    public const int MaxPageSize = 10000;

    [ConnectorProperty(ConnectorPropertyKey.MaxCount, DefaultValue = "1000", MinValue = 100, MaxValue = MaxPageSize)]
    public int MaxCount { get; set; } = 1000;

    [ConnectorProperty(ConnectorPropertyKey.WriteBackEnabled, DefaultValue = "false")]
    public bool WriteBackEnabled { get; set; } = false;

    [ConnectorProperty(ConnectorPropertyKey.WriteBackBatchSize, DefaultValue = "50", MinValue = 1, MaxValue = 500)]
    public int WriteBackBatchSize { get; set; } = 50;
}
