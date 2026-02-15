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
    SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.Treatments]
)]
public class NightscoutConnectorConfiguration : BaseConnectorConfiguration
{
    public NightscoutConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Nightscout;
    }

    [ConnectorProperty("Url",
        Required = true,
        RuntimeConfigurable = true,
        Category = "Connection",
        Description = "Nightscout site URL (e.g., https://my-nightscout.herokuapp.com)",
        Format = "uri")]
    public string Url { get; set; } = string.Empty;

    [ConnectorProperty("ApiSecret",
        Required = true,
        Secret = true,
        Description = "Nightscout API_SECRET or access token")]
    public string ApiSecret { get; set; } = string.Empty;

    [ConnectorProperty("SyncTreatments",
        RuntimeConfigurable = true,
        Category = "Sync",
        Description = "Whether to sync treatments from Nightscout",
        DefaultValue = "true")]
    public bool SyncTreatments { get; set; } = true;

    [ConnectorProperty("MaxCount",
        RuntimeConfigurable = true,
        Category = "Advanced",
        Description = "Maximum number of records to fetch per request",
        DefaultValue = "1000",
        MinValue = 100,
        MaxValue = 10000)]
    public int MaxCount { get; set; } = 1000;
}
