using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps decoded CGM data events (Dexcom G6/G7, Libre 2/3) to <see cref="SensorGlucose"/> records.
/// Mirrors <c>tconnectsync</c>'s <c>process_cgm_reading.py</c>: each reading is timestamped by its
/// embedded EGV timestamp (which is correct for back-filled readings) rather than the event's
/// store time, and out-of-range readings are reported as the Nightscout LOW/HIGH sentinels.
/// </summary>
public sealed class TandemCgmMapper(ILogger logger, TandemTimeResolver time)
{
    // Sentinels for out-of-range readings, mirroring the Tandem Source frontend's
    // CgmBuilder.determineGlucoseValue (via tconnectsync's determine_glucose_value).
    private const double GlucoseLimitLow = 40;
    private const double GlucoseLimitHigh = 400;
    private const double GlucoseValueLow = 39;
    private const double GlucoseValueHigh = 401;

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<SensorGlucose> Map(IEnumerable<TandemPumpEvent> events)
    {
        var now = DateTime.UtcNow;
        var records = new List<SensorGlucose>();

        foreach (var ev in events)
        {
            var mgdl = ResolveGlucoseValue(ev);
            if (mgdl == null)
                continue;

            var egvSeconds = ev.Raw("EGV TimeStamp") ?? 0;
            var timestamp = egvSeconds > 0 ? _time.ToUtc(egvSeconds) : _time.ToUtc(ev.RawTimestampSeconds);

            records.Add(new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                UtcOffset = _time.OffsetMinutes,
                Device = TandemMapHelpers.Source,
                DataSource = TandemMapHelpers.Source,
                SyncIdentifier = $"tandem_cgm_{ev.SeqNum}",
                LegacyId = $"tandem_cgm_{ev.SeqNum}",
                Mgdl = mgdl.Value,
                TrendRate = ev.Num("Rate"),
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        _logger.LogDebug("Mapped {Count} Tandem CGM readings", records.Count);
        return records;
    }

    /// <summary>
    /// The value to publish for a reading: the LOW (39) / HIGH (401) sentinel for special or
    /// out-of-range statuses, the raw display value otherwise, or null to skip. The raw
    /// glucoseValueStatus values (0 precise, 1 special-high, 2 special-low) are consistent across
    /// the G6/G7/FSL2/FSL3 schemas even though each names its enum members differently.
    /// </summary>
    private static double? ResolveGlucoseValue(TandemPumpEvent ev)
    {
        var display = ev.Num("currentGlucoseDisplayValue") ?? 0;
        return ev.Raw("glucoseValueStatus") switch
        {
            1 => GlucoseValueHigh,
            2 => GlucoseValueLow,
            0 when display < GlucoseLimitLow => GlucoseValueLow,
            0 when display > GlucoseLimitHigh => GlucoseValueHigh,
            // Other/absent status with no usable display value (e.g. G7's "Do Not Show"): skip
            // rather than publish a 0 reading.
            _ when display <= 0 => null,
            _ => display,
        };
    }
}
