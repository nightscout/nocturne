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
    DefaultActiveThresholdMinutes = 180,
    DefaultStaleThresholdMinutes = 360,
    SupportedDataTypes = [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.BasalInjections,
        SyncDataType.CarbIntake,
        SyncDataType.Food,
        SyncDataType.TempBasals,
        SyncDataType.StateSpans,
        SyncDataType.TempBasals,
        SyncDataType.DeviceEvents,
        SyncDataType.Profiles,
        SyncDataType.Notes,
        SyncDataType.Activity
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

    /// <summary>
    ///     How many days back a background sync reaches. Glooko receives pump data in batches, days
    ///     after the fact, so the window is a fixed lookback rather than a resume point at the newest
    ///     stored record; anything that arrives later than this is picked up by the daily full walk
    ///     over <see cref="GlookoConstants.FullWalkMonths"/>. Ten days plus the one-day padding on
    ///     each side of the request fits one <see cref="GlookoConstants.SyncChunkSize"/> chunk (two
    ///     requests) with room for the clock and a timezone offset change inside the window. The
    ///     ceiling keeps a scheduled run to a handful of chunks — the full history is the walk's job.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.LookbackDays, DefaultValue = "10", MinValue = 1, MaxValue = 60)]
    public int LookbackDays { get; set; } = 10;

    /// <summary>
    ///     Sync via Glooko's granular SSV2 cursor protocol — the per-resource <c>/api/v2/{resource}</c>
    ///     endpoints the mobile app uses — instead of the date-windowed web graph/batch flow. When enabled
    ///     this path sources <em>every</em> data type incrementally by persisted per-resource cursor:
    ///     glucose from the raw per-reading <c>cgm/egvs</c> stream, plus boluses, basals, meter readings,
    ///     foods and device events (the latter from the net-new <c>pumps/events</c> feed). The windowed
    ///     v2/v3 path is bypassed entirely, so <see cref="UseV3Api"/> has no effect while this is on.
    ///     Experimental; off by default.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.UseSsv2Sync, DefaultValue = "false")]
    public bool UseSsv2Sync { get; set; } = false;
}
