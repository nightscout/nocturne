using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// Converts raw Tandem pump timestamps (seconds since 2008-01-01, expressed in the user's local
/// wall-clock with no timezone attached) into UTC. Tandem event timestamps carry no offset — the
/// pump records local wall-clock — so, like <c>tconnectsync</c>'s <c>TIMEZONE_NAME</c> handling, the
/// connector reads them in the account's configured zone to recover UTC.
/// </summary>
/// <remarks>
/// With a zone, each wall-clock value is read with the offset that zone had at that local time, by
/// <see cref="TimeZoneHelper.LocalToUtc"/>: a time that occurs twice when clocks go back resolves to
/// the first occurrence (the daylight offset), and a time skipped when clocks go forward resolves to
/// the first instant after the gap. Without one, every value is read with the fixed offset.
/// <para>
/// A t:slim X2 has no zone of its own: its clock changes when the user sets it, not at the DST
/// instant, so events between the two are still read with the wrong offset.
/// </para>
/// </remarks>
public sealed class TandemTimeResolver
{
    private readonly TimeZoneInfo? _zone;
    private readonly TimeSpan _fixedOffset;

    /// <param name="timezoneOffsetHours">The fixed offset used when <paramref name="timeZoneId"/> is absent.</param>
    /// <param name="timeZoneId">
    /// An IANA (or Windows) zone id. An id that does not resolve is treated as absent.
    /// </param>
    public TandemTimeResolver(double timezoneOffsetHours, string? timeZoneId = null)
    {
        _fixedOffset = TimeSpan.FromMinutes(Math.Round(timezoneOffsetHours * 60));
        if (TimeZoneHelper.TryGetTimeZoneInfoFromId(timeZoneId?.Trim(), out var zone))
            _zone = zone;
    }

    /// <summary>Whether timestamps are read in a time zone rather than with the fixed offset.</summary>
    public bool UsesTimeZone => _zone is not null;

    /// <summary>Converts a raw Tandem timestamp (seconds since the Tandem epoch) to UTC.</summary>
    public DateTime ToUtc(long rawSeconds) =>
        ToUtc(DateTimeOffset
            .FromUnixTimeSeconds(TandemConstants.TandemEpochUnixSeconds + rawSeconds)
            .UtcDateTime);

    /// <summary>
    /// Converts a naive pump-local wall-clock value (e.g. the BFF's <c>maxDateOfEvents</c> /
    /// <c>availableDataRange</c>, which carry no timezone) to UTC. Only its date and time are read.
    /// </summary>
    public DateTime ToUtc(DateTime wallClock)
    {
        var wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return _zone is { } zone
            ? TimeZoneHelper.LocalToUtc(wall, zone)
            : DateTime.SpecifyKind(wall - _fixedOffset, DateTimeKind.Utc);
    }

    /// <summary>The UTC offset in whole minutes in effect at <paramref name="utc"/>, for a record's <c>UtcOffset</c>.</summary>
    public int OffsetMinutesAt(DateTime utc) =>
        (int)(_zone?.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) ?? _fixedOffset).TotalMinutes;
}
