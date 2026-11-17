using Nocturne.Services.Demo.Services;

namespace Nocturne.API.Services.Seeding;

/// <summary>
/// The wall clock of a seed run: the instant it started and the tenant's zone, in which the seeder's
/// own rows and every generated stream lay out their days.
/// </summary>
internal sealed class SeedingClock
{
    private const string FallbackZoneId = "Australia/Sydney";

    private SeedingClock(string zoneId, DemoClock demo, DateTime utcNow)
    {
        ZoneId = zoneId;
        Demo = demo;
        UtcNow = utcNow;
    }

    /// <summary>The IANA id written to the patient record, the profile and the timezone timeline.</summary>
    public string ZoneId { get; }

    /// <summary>
    /// The frame handed to the demo generator, in the zone <see cref="ZoneId"/> names. Its own now reads
    /// the provider live; the run's instant is <see cref="UtcNow"/>.
    /// </summary>
    public DemoClock Demo { get; }

    /// <summary>The instant the run started.</summary>
    public DateTime UtcNow { get; }

    /// <summary>The tenant's wall-clock time at <see cref="UtcNow"/>, Unspecified kind.</summary>
    public DateTime LocalNow => Demo.ToLocal(UtcNow);

    /// <summary>The tenant's local midnight, Unspecified kind.</summary>
    public DateTime LocalToday => LocalNow.Date;

    /// <summary>
    /// Resolves the zone a seed lays its days out in: <paramref name="recordedZoneId"/> when the
    /// patient record already names a zone this host knows, else the provider's local zone, which the
    /// seed then writes to the record. A provider zone with no IANA id falls back to
    /// <see cref="FallbackZoneId"/>, for both the record and the layout.
    /// </summary>
    public static SeedingClock Resolve(string? recordedZoneId, TimeProvider time)
    {
        var utcNow = time.GetUtcNow().UtcDateTime;

        if (recordedZoneId is { Length: > 0 } && TryFind(recordedZoneId) is { } recorded)
            return new SeedingClock(recordedZoneId, new DemoClock(time, recorded), utcNow);

        if (IanaId(time.LocalTimeZone) is { } hostZoneId)
            return new SeedingClock(hostZoneId, new DemoClock(time, time.LocalTimeZone), utcNow);

        return new SeedingClock(
            FallbackZoneId, new DemoClock(time, TimeZoneInfo.FindSystemTimeZoneById(FallbackZoneId)), utcNow);
    }

    /// <summary>A tenant wall-clock time as UTC; see <see cref="DemoClock.ToUtc"/>.</summary>
    public DateTime ToUtc(DateTime local) => Demo.ToUtc(local);

    /// <summary>
    /// The zone as an IANA id: a Windows host reports a Windows id, which timezone pickers and
    /// profile consumers don't recognise.
    /// </summary>
    private static string? IanaId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
            return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
