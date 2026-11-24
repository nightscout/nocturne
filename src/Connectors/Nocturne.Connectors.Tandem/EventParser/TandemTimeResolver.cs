using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Timezones;

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
/// the first instant after the gap. The zone is, in order, the connector's own, then the fixed
/// offset when it is not zero, then the tenant's time zone history when it has entries, then the
/// tenant's time zone. An offset of zero is the unset value, because a
/// saved connector configuration carries zero whether or not anyone chose it; a pump on UTC sets its
/// zone to <c>UTC</c>. With none of the four, every value is read as UTC. With a history, only a
/// time no entry covers is read in the tenant's zone, or as UTC without one.
/// <para>
/// A t:slim X2 has no zone of its own: its clock changes when the user sets it, not at the DST
/// instant, so events between the two are still read with the wrong offset.
/// </para>
/// </remarks>
public sealed class TandemTimeResolver
{
    private readonly TimeZoneInfo? _zone;
    private readonly TimeSpan _fixedOffset;
    private readonly TimezoneTimeline? _timeline;

    /// <param name="timezoneOffsetHours">
    /// The fixed offset used when <paramref name="timeZoneId"/> is absent. Zero is unset.
    /// </param>
    /// <param name="timeZoneId">
    /// An IANA (or Windows) zone id. An id that does not resolve is treated as absent.
    /// </param>
    /// <param name="tenantTimeZoneId">
    /// The tenant's zone, used when neither setting is present. An id that does not resolve is
    /// treated as absent.
    /// </param>
    /// <param name="tenantTimeline">
    /// The tenant's time zone history, used ahead of <paramref name="tenantTimeZoneId"/> when it has
    /// an entry in effect at <paramref name="asOf"/>. A time no entry covers is read in
    /// <paramref name="tenantTimeZoneId"/>, or as UTC without one.
    /// </param>
    /// <param name="asOf">Now, which names the zone shown for a history.</param>
    public TandemTimeResolver(
        double timezoneOffsetHours,
        string? timeZoneId = null,
        string? tenantTimeZoneId = null,
        TimezoneTimeline? tenantTimeline = null,
        DateTime asOf = default)
    {
        _fixedOffset = TimeSpan.FromMinutes(Math.Round(timezoneOffsetHours * 60));

        if (TimeZoneHelper.TryGetTimeZoneInfoFromId(timeZoneId?.Trim(), out var zone))
        {
            _zone = zone;
            ResolvedZone = ZoneOf(zone, ConnectorTimeZoneSource.ConnectorZone);
        }
        else if (_fixedOffset != TimeSpan.Zero)
        {
            ResolvedZone = new ConnectorTimeZone
            {
                Id = $"UTC{(_fixedOffset < TimeSpan.Zero ? '-' : '+')}{_fixedOffset.Duration():hh':'mm}",
                Source = ConnectorTimeZoneSource.ConnectorOffset,
            };
        }
        else if (tenantTimeline is { HasEntries: true } && tenantTimeline.ZoneAt(asOf) is { } timelineZoneId)
        {
            _timeline = tenantTimeline;
            if (TimeZoneHelper.TryGetTimeZoneInfoFromId(tenantTimeZoneId?.Trim(), out var uncovered))
                _zone = uncovered;
            ResolvedZone = new ConnectorTimeZone
            {
                Id = TimeZoneHelper.ToIanaIdOrNull(timelineZoneId) ?? timelineZoneId,
                Source = ConnectorTimeZoneSource.TenantTimeline,
            };
        }
        else if (TimeZoneHelper.TryGetTimeZoneInfoFromId(tenantTimeZoneId?.Trim(), out zone))
        {
            _zone = zone;
            ResolvedZone = ZoneOf(zone, ConnectorTimeZoneSource.TenantZone);
        }
        else
        {
            ResolvedZone = new ConnectorTimeZone { Id = "UTC", Source = ConnectorTimeZoneSource.Utc };
        }
    }

    /// <summary>The zone the timestamps are read in, and which setting named it.</summary>
    public ConnectorTimeZone ResolvedZone { get; }

    /// <summary>
    /// Whether the connector's own settings name no zone, so the tenant's zone decides what they are read in.
    /// </summary>
    public static bool NeedsTenantZone(double timezoneOffsetHours, string? timeZoneId) =>
        TimeSpan.FromMinutes(Math.Round(timezoneOffsetHours * 60)) == TimeSpan.Zero
        && !TimeZoneHelper.TryGetTimeZoneInfoFromId(timeZoneId?.Trim(), out _);

    private static ConnectorTimeZone ZoneOf(TimeZoneInfo zone, ConnectorTimeZoneSource source) =>
        new() { Id = TimeZoneHelper.ToIanaIdOrNull(zone.Id) ?? zone.Id, Source = source };

    /// <summary>
    /// Whether some timestamps are read as UTC for want of any zone: no zone anywhere, or a zone
    /// history that leaves some instant uncovered and no tenant zone to cover it.
    /// </summary>
    public bool ReadsAsUtc => ResolvedZone.Source == ConnectorTimeZoneSource.Utc || (_timeline is not null && _zone is null && _timeline.ZoneAt(DateTime.MinValue) is null);

    /// <summary>Whether timestamps are read in a time zone rather than with the fixed offset.</summary>
    public bool UsesTimeZone => _zone is not null || _timeline is not null;

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
        if (_timeline is not null)
            return _zone is { } uncovered && _timeline.ZoneAt(wall) is null
                ? TimeZoneHelper.LocalToUtc(wall, uncovered)
                : _timeline.ToUtc(wall);

        return _zone is { } zone
            ? TimeZoneHelper.LocalToUtc(wall, zone)
            : DateTime.SpecifyKind(wall - _fixedOffset, DateTimeKind.Utc);
    }

    /// <summary>The UTC offset in whole minutes in effect at <paramref name="utc"/>, for a record's <c>UtcOffset</c>.</summary>
    public int OffsetMinutesAt(DateTime utc)
    {
        if (_timeline is not null && (_zone is null || _timeline.ZoneAt(utc) is not null))
            return _timeline.OffsetMinutesAtUtc(utc);

        return (int)(_zone?.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) ?? _fixedOffset).TotalMinutes;
    }
}
