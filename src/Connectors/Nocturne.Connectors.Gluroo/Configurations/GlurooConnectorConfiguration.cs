using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Gluroo.Configurations;

[ConnectorRegistration(
    "Gluroo",
    ServiceNames.GlurooConnector,
    "GLUROO",
    "ConnectSource.Gluroo",
    "gluroo-connector",
    "gluroo",
    ConnectorCategory.Sync,
    "Sync glucose, treatment, and profile data from Gluroo Global Connect",
    "Gluroo",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 90,
    SupportsManualSync = true,
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.Notes,
        SyncDataType.Profiles
    ]
)]
public class GlurooConnectorConfiguration : NightscoutConnectorConfiguration
{
    public GlurooConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Gluroo;
    }
}
