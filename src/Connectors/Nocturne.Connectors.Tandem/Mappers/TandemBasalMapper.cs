using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps Tandem basal-delivery events (emitted roughly every five minutes) to <see cref="TempBasal"/>
/// spans. Each span runs from one delivery event to the next, with the final span ending at the
/// window end. Mirrors <c>tconnectsync</c>'s <c>process_basal.py</c>, including the
/// <c>IGNORE_ZERO_UNIT_BASAL</c> behaviour.
/// </summary>
public sealed class TandemBasalMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<TempBasal> Map(IEnumerable<TandemPumpEvent> events, DateTime windowEndUtc, bool ignoreZeroUnitBasal)
    {
        var ordered = events
            .Select(ev => (Event: ev, Start: _time.ToUtc(ev.RawTimestampSeconds)))
            .OrderBy(x => x.Start)
            .ToList();

        var now = DateTime.UtcNow;
        var records = new List<TempBasal>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var (ev, start) = ordered[i];
            var end = i < ordered.Count - 1 ? ordered[i + 1].Start : windowEndUtc;

            var rate = TandemMapHelpers.MilliunitsToUnits(ev.Num("Commanded Rate") ?? 0);
            if (ignoreZeroUnitBasal && rate < 0.01)
                continue;

            records.Add(new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = start,
                EndTimestamp = end > start ? end : null,
                UtcOffset = _time.OffsetMinutes,
                Device = TandemMapHelpers.Source,
                DataSource = TandemMapHelpers.Source,
                LegacyId = $"tandem_basal_{ev.SeqNum}",
                PumpRecordId = ev.SeqNum.ToString(),
                Rate = rate,
                Origin = MapOrigin(ev.EnumName("Commanded Rate Source")),
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        _logger.LogDebug("Mapped {Count} Tandem temp basals", records.Count);
        return records;
    }

    private static TempBasalOrigin MapOrigin(string? source) => source switch
    {
        "Suspended" => TempBasalOrigin.Suspended,
        "Profile" => TempBasalOrigin.Scheduled,
        "Temp Rate" => TempBasalOrigin.Manual,
        "Algorithm" => TempBasalOrigin.Algorithm,
        "Temp Rate and Algorithm" => TempBasalOrigin.Algorithm,
        _ => TempBasalOrigin.Scheduled,
    };
}
