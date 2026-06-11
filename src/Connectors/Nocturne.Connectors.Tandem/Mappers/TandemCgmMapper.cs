using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps decoded CGM data events (Dexcom G6/G7, Libre 2/3) to <see cref="SensorGlucose"/> records.
/// Mirrors <c>tconnectsync</c>'s <c>process_cgm_reading.py</c>: each reading is timestamped by its
/// embedded EGV timestamp (which is correct for back-filled readings) rather than the event's
/// store time.
/// </summary>
public sealed class TandemCgmMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<SensorGlucose> Map(IEnumerable<TandemPumpEvent> events)
    {
        var now = DateTime.UtcNow;
        var records = new List<SensorGlucose>();

        foreach (var ev in events)
        {
            var mgdl = ev.Num("currentGlucoseDisplayValue") ?? 0;
            if (mgdl <= 0)
                // High/Low statuses report a display value of 0; skip rather than publish a 0 reading.
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
                Mgdl = mgdl,
                TrendRate = ev.Num("Rate"),
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        _logger.LogDebug("Mapped {Count} Tandem CGM readings", records.Count);
        return records;
    }
}
