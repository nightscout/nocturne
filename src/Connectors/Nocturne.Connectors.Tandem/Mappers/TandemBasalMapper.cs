using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>The spans a payload yields, and the one it holds no end for.</summary>
/// <param name="Spans">The spans, each closed by the delivery event that follows it.</param>
/// <param name="UnclosedFrom">
///     The start of the span the payload cannot close — the last event's, when nothing above it was
///     fetched — or <c>null</c> when every span has an end. That span is absent from
///     <paramref name="Spans"/>, because the records upsert on a stable id and any end invented for
///     it (the fetch's upper bound, or the pump's newest event) overwrites the real one already
///     stored, either shortening the span or stretching it over days it did not run.
/// </param>
public readonly record struct TandemBasalSpans(List<TempBasal> Spans, DateTime? UnclosedFrom);

/// <summary>
/// Maps Tandem basal-delivery events (emitted roughly every five minutes) to <see cref="TempBasal"/>
/// spans. Each span runs from one delivery event to the next, with the final span ending at the
/// point the fetch is complete through. Mirrors <c>tconnectsync</c>'s <c>process_basal.py</c>,
/// including the <c>IGNORE_ZERO_UNIT_BASAL</c> behaviour.
/// </summary>
public sealed class TandemBasalMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <param name="fetchedThroughUtc">
    ///     The point the payload is complete through, which closes the final span, or <c>null</c>
    ///     when the fetch stopped short of the pump's newest event and that span's successor was
    ///     never fetched — see <see cref="TandemBasalSpans.UnclosedFrom"/>.
    /// </param>
    public TandemBasalSpans Map(
        IEnumerable<TandemPumpEvent> events, DateTime? fetchedThroughUtc, bool ignoreZeroUnitBasal)
    {
        var ordered = events
            .Select(ev => (Event: ev, Start: _time.ToUtc(ev.RawTimestampSeconds)))
            .OrderBy(x => x.Start)
            .ToList();

        var now = DateTime.UtcNow;
        var records = new List<TempBasal>();
        DateTime? unclosedFrom = null;

        for (var i = 0; i < ordered.Count; i++)
        {
            var (ev, start) = ordered[i];

            var rate = TandemMapHelpers.MilliunitsToUnits(ev.Num("Commanded Rate") ?? 0);
            if (ignoreZeroUnitBasal && rate < 0.01)
                continue;

            var end = i < ordered.Count - 1 ? ordered[i + 1].Start : fetchedThroughUtc;
            if (end is null)
            {
                unclosedFrom = start;
                continue;
            }

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
        return new TandemBasalSpans(records, unclosedFrom);
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
