using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Nightscout.Configurations;

[ConnectorRegistration(
    "Nightscout",
    ServiceNames.NightscoutConnector,
    "NIGHTSCOUT",
    "ConnectSource.Nightscout",
    DataSources.NightscoutConnector,
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

    [ConnectorProperty(ConnectorPropertyKey.SyncIntervalMinutes, AllowedNumbers = [1, 5, 60, 240])]
    public override int SyncIntervalMinutes { get; set; } = 5;

    [ConnectorProperty(ConnectorPropertyKey.Url, Required = true, Format = "uri")]
    public string Url { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.RealtimeUrl, Format = "uri")]
    public string RealtimeUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Optional: a site can be readable without one (open to read, a default role that reads,
    ///     or a proxy in front of it that admits the request). Blank sends no <c>api-secret</c>.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.ApiSecret, Secret = true)]
    public virtual string ApiSecret { get; set; } = string.Empty;

    /// <summary>The largest page the connector asks the source for.</summary>
    public const int MaxPageSize = BackwardTimePager.MaxWidestPageSize;

    [ConnectorProperty(ConnectorPropertyKey.MaxCount, DefaultValue = "1000", MinValue = 100, MaxValue = MaxPageSize)]
    public int MaxCount { get; set; } = 1000;

    /// <summary>The <see cref="HistoryDays"/> that sets no limit.</summary>
    public const int WholeHistory = 0;

    /// <summary>The longest <see cref="HistoryDays"/> read as a limit; a saved value past it reads as <see cref="WholeHistory"/>.</summary>
    public const int LongestHistoryDays = 365;

    /// <summary>
    ///     How many days of the source's history are imported, by the connector's first backfill and
    ///     by the import started from it; <see cref="WholeHistory"/> for all of it. The default sets
    ///     no limit: a configuration saved before the setting existed reads as the default, and it
    ///     was importing everything.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.HistoryDays, DefaultValue = "0", AllowedNumbers = [30, 90, 365, WholeHistory])]
    public int HistoryDays { get; set; } = WholeHistory;

    /// <summary>
    ///     The oldest instant a history of <paramref name="historyDays"/> reaches back to from
    ///     <paramref name="now"/>, or null for the whole history.
    /// </summary>
    public static DateTime? HistoryStart(int historyDays, DateTime now) =>
        historyDays is > WholeHistory and <= LongestHistoryDays ? now.AddDays(-historyDays) : null;

    [ConnectorProperty(ConnectorPropertyKey.WriteBackEnabled, DefaultValue = "false", SendsRecords = true)]
    public bool WriteBackEnabled { get; set; } = false;

    [ConnectorProperty(ConnectorPropertyKey.WriteBackBatchSize, DefaultValue = "50", MinValue = 1, MaxValue = 500)]
    public int WriteBackBatchSize { get; set; } = 50;
}
