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
    "utensils",
    ConnectorCategory.Nutrition,
    "Sync food diary entries from MyFitnessPal for meal matching",
    "MyFitnessPal",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 365,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Food]
)]
public class MyFitnessPalConnectorConfiguration : BaseConnectorConfiguration
{
    public MyFitnessPalConnectorConfiguration()
    {
        ConnectSource = ConnectSource.MyFitnessPal;
        SyncIntervalMinutes = 15;
    }

    [ConnectorProperty(
        "Username",
        Required = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "MyFitnessPal username"
    )]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty(
        "LookbackDays",
        RuntimeConfigurable = true,
        Category = "Sync",
        Description = "Number of days to look back when syncing",
        DefaultValue = "7",
        MinValue = 1,
        MaxValue = 365
    )]
    public int LookbackDays { get; set; } = 7;
}
