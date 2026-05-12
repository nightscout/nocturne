using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.MyLife.Configurations;

[ConnectorRegistration(
    "MyLife",
    "MYLIFE",
    "MYLIFE",
    "ConnectSource.MyLife",
    "mylife-connector",
    "mylife",
    ConnectorCategory.Pump,
    "Connect to MyLife for pump data",
    "MyLife",
    SupportsHistoricalSync = true,
    SupportsManualSync = true,
    DefaultActiveThresholdMinutes = 180,
    DefaultStaleThresholdMinutes = 360,
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.Notes,
        SyncDataType.DeviceEvents,
        SyncDataType.StateSpans
    ]
)]
public class MyLifeConnectorConfiguration : BaseConnectorConfiguration
{
    public MyLifeConnectorConfiguration()
    {
        ConnectSource = ConnectSource.MyLife;
    }

    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.PatientId)]
    public string PatientId { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.ServiceUrl, Format = "uri")]
    public string ServiceUrl { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.EnableMealCarbConsolidation, DefaultValue = "true")]
    public bool EnableMealCarbConsolidation { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.EnableTempBasalConsolidation, DefaultValue = "true")]
    public bool EnableTempBasalConsolidation { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.TempBasalConsolidationWindowMinutes, DefaultValue = "5", MinValue = 1, MaxValue = 30)]
    public int TempBasalConsolidationWindowMinutes { get; set; } = 5;

    [ConnectorProperty(ConnectorPropertyKey.AppPlatform, DefaultValue = "2")]
    public int AppPlatform { get; set; } = 2;

    [ConnectorProperty(ConnectorPropertyKey.AppVersion, DefaultValue = "20403")]
    public int AppVersion { get; set; } = 20403;
}
