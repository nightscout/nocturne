using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.MyFitnessPal.Configurations;

[ConnectorRegistration(
    "MyFitnessPal",
    ServiceNames.MyFitnessPalConnector,
    "MYFITNESSPAL",
    "ConnectSource.MyFitnessPal",
    "myfitnesspal-connector",
    "myfitnesspal",
    ConnectorCategory.Nutrition,
    "Sync food diary entries from MyFitnessPal for meal matching",
    "MyFitnessPal",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 365,
    SupportsManualSync = true,
    DefaultActiveThresholdMinutes = 180,
    DefaultStaleThresholdMinutes = 360,
    SupportedDataTypes = [SyncDataType.Food]
)]
public class MyFitnessPalConnectorConfiguration : BaseConnectorConfiguration
{
    public MyFitnessPalConnectorConfiguration()
    {
        ConnectSource = ConnectSource.MyFitnessPal;
        SyncIntervalMinutes = 15;
    }

    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.LookbackDays, DefaultValue = "7", MinValue = 1, MaxValue = 365)]
    public int LookbackDays { get; set; } = 7;
}
