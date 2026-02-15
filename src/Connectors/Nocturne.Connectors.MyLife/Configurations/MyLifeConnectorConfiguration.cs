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
    SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.Treatments]
)]
public class MyLifeConnectorConfiguration : BaseConnectorConfiguration
{
    public MyLifeConnectorConfiguration()
    {
        ConnectSource = ConnectSource.MyLife;
    }

    [ConnectorProperty("Username",
        Required = true,
        Secret = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "MyLife account username")]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty("Password",
        Required = true,
        Secret = true,
        Description = "MyLife account password")]
    public string Password { get; set; } = string.Empty;

    [ConnectorProperty("PatientId",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Patient id for MyLife")]
    public string PatientId { get; set; } = string.Empty;

    [ConnectorProperty("ServiceUrl",
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Override MyLife service url",
        Format = "uri")]
    public string ServiceUrl { get; set; } = string.Empty;

    [ConnectorProperty("EnableGlucoseSync",
        RuntimeConfigurable = true,
        Category = "Sync",
        Description = "Enable CGM glucose sync",
        DefaultValue = "true")]
    public bool EnableGlucoseSync { get; set; } = true;

    [ConnectorProperty("EnableManualBgSync",
        RuntimeConfigurable = true,
        Category = "Sync",
        Description = "Enable manual BG sync",
        DefaultValue = "true")]
    public bool EnableManualBgSync { get; set; } = true;

    [ConnectorProperty("EnableMealCarbConsolidation",
        RuntimeConfigurable = true,
        Category = "Advanced",
        Description = "Enable meal carb and bolus consolidation",
        DefaultValue = "true")]
    public bool EnableMealCarbConsolidation { get; set; } = true;

    [ConnectorProperty("EnableTempBasalConsolidation",
        RuntimeConfigurable = true,
        Category = "Advanced",
        Description = "Enable temp basal consolidation",
        DefaultValue = "true")]
    public bool EnableTempBasalConsolidation { get; set; } = true;

    [ConnectorProperty("TempBasalConsolidationWindowMinutes",
        RuntimeConfigurable = true,
        Category = "Advanced",
        Description = "Temp basal consolidation window in minutes",
        DefaultValue = "5",
        MinValue = 1,
        MaxValue = 30)]
    public int TempBasalConsolidationWindowMinutes { get; set; } = 5;

    [ConnectorProperty("AppPlatform",
        Description = "MyLife app platform",
        DefaultValue = "2")]
    public int AppPlatform { get; set; } = 2;

    [ConnectorProperty("AppVersion",
        Description = "MyLife app version",
        DefaultValue = "20403")]
    public int AppVersion { get; set; } = 20403;

}
