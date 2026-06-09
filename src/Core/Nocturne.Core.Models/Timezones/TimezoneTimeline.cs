namespace Nocturne.Core.Models.Timezones;

/// <summary>
/// Resolves a connector's "fake UTC" timestamps — local wall-clock stamped as if UTC — to true UTC,
/// using an ordered timezone timeline. For each timestamp it picks the timeline entry in effect at
/// that local instant and converts via <see cref="TimeZoneInfo"/>, so daylight saving is applied per
/// the zone's historical rules for that date and travel/relocation is honoured by successive entries.
/// </summary>
/// <remarks>
/// Matching is done in <b>local wall-clock</b> space (the same space the fake-UTC value and the
/// entries' <see cref="TimezoneTimelineEntry.EffectiveFrom"/> live in), so there is no chicken-and-egg
/// between "need the zone to get UTC" and "need the instant to pick the zone".
///
/// When the timeline is empty, or a timestamp predates the earliest entry, the resolver falls back to
/// an optional legacy fixed offset (the connector's historical behaviour); with no offset it treats
/// the value as already-UTC. This keeps existing tenants from regressing before an origin entry is seeded.
/// </remarks>
/// <seealso cref="TimezoneTimelineEntry"/>
/// <seealso cref="Nocturne.Core.Models.TimeZoneHelper"/>
public sealed class TimezoneTimeline
{
    // Entries sorted by EffectiveFrom descending, so the first whose EffectiveFrom <= the
    // queried wall-clock is the one in effect.
    private readonly IReadOnlyList<TimezoneTimelineEntry> _entriesDesc;
    private readonly double? _fallbackOffsetHours;

    /// <summary>
    /// Creates a timeline resolver.
    /// </summary>
    /// <param name="entries">The timeline entries (any order).</param>
    /// <param name="fallbackOffsetHours">
    /// Legacy fixed offset (hours east of UTC) applied when no entry covers a timestamp. When null and
    /// no entry applies, the timestamp is treated as already-UTC.
    /// </param>
    public TimezoneTimeline(IEnumerable<TimezoneTimelineEntry> entries, double? fallbackOffsetHours = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entriesDesc = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Timezone))
            .OrderByDescending(e => e.EffectiveFrom)
            .ToList();
        _fallbackOffsetHours = fallbackOffsetHours;
    }

    /// <summary>Whether the timeline has at least one usable entry.</summary>
    public bool HasEntries => _entriesDesc.Count > 0;

    /// <summary>
    /// Converts a fake-UTC timestamp (local wall-clock stamped as UTC) to true UTC.
    /// </summary>
    /// <param name="fakeUtc">
    /// The connector timestamp. Only its wall-clock components are used (the kind is ignored), since the
    /// value is the local clock reading dressed up as UTC.
    /// </param>
    /// <returns>The true UTC instant (<see cref="DateTimeKind.Utc"/>).</returns>
    public DateTime ToUtc(DateTime fakeUtc)
    {
        var wall = DateTime.SpecifyKind(fakeUtc, DateTimeKind.Unspecified);

        var entry = ResolveEntry(wall);
        if (entry is null)
        {
            if (_fallbackOffsetHours is { } offset)
                return DateTime.SpecifyKind(wall.AddHours(-offset), DateTimeKind.Utc);

            return DateTime.SpecifyKind(wall, DateTimeKind.Utc);
        }

        var tz = TimeZoneHelper.GetTimeZoneInfoFromId(entry.Timezone);
        return LocalToUtc(wall, tz);
    }

    /// <summary>
    /// Returns the IANA/Windows timezone id in effect at the given local wall-clock instant, or null
    /// when no entry covers it (timeline empty or instant predates the earliest entry).
    /// </summary>
    public string? ZoneAt(DateTime fakeUtc) =>
        ResolveEntry(DateTime.SpecifyKind(fakeUtc, DateTimeKind.Unspecified))?.Timezone;

    private TimezoneTimelineEntry? ResolveEntry(DateTime wallUnspecified)
    {
        // Entries are descending by EffectiveFrom, so the first one at or before the instant wins.
        foreach (var entry in _entriesDesc)
        {
            if (DateTime.SpecifyKind(entry.EffectiveFrom, DateTimeKind.Unspecified) <= wallUnspecified)
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Converts an unspecified local wall-clock to UTC in the given zone, defensively handling the two
    /// daylight-saving edge hours so a reading is never dropped:
    /// <list type="bullet">
    /// <item>Spring-forward gap (a local time that never existed): nudge forward past the gap.</item>
    /// <item>Fall-back overlap (a local time that occurred twice): take the standard-time interpretation
    /// (<see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>'s default).</item>
    /// </list>
    /// </summary>
    private static DateTime LocalToUtc(DateTime wall, TimeZoneInfo tz)
    {
        if (tz.IsInvalidTime(wall))
        {
            // The clock sprang forward over this local time; advance to the first instant that exists.
            var nudged = wall;
            for (var i = 0; i < 4 && tz.IsInvalidTime(nudged); i++)
                nudged = nudged.AddHours(1);

            wall = nudged;
        }

        return TimeZoneInfo.ConvertTimeToUtc(wall, tz);
    }
}
