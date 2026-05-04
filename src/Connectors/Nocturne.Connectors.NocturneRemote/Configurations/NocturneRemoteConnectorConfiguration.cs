using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.NocturneRemote.Configurations;

[ConnectorRegistration(
    "NocturneRemote",
    ServiceNames.NocturneRemoteConnector,
    "NOCTURNE_REMOTE",
    "ConnectSource.NocturneRemote",
    "nocturne-remote-connector",
    "nocturne",
    ConnectorCategory.Sync,
    "Import data from a remote Nocturne instance",
    "Nocturne Remote",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 0,
    SupportsManualSync = true,
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.Notes,
        SyncDataType.DeviceEvents,
        SyncDataType.StateSpans,
        SyncDataType.Profiles,
        SyncDataType.DeviceStatus,
        SyncDataType.Activity,
        SyncDataType.Food
    ]
)]
public class NocturneRemoteConnectorConfiguration : BaseConnectorConfiguration
{
    public NocturneRemoteConnectorConfiguration()
    {
        ConnectSource = ConnectSource.NocturneRemote;
    }

    /// <summary>
    ///     Remote Nocturne instance base URL.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Url, Required = true, Format = "uri")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    ///     Direct grant bearer token for the remote instance.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.ApiSecret, Required = true, Secret = true)]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    ///     Page size for paginated V4 API requests.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.MaxCount, DefaultValue = "500", MinValue = 50, MaxValue = 5000)]
    public int MaxCount { get; set; } = 500;
}
