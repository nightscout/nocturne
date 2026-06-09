using Nocturne.Core.Models.Timezones;

namespace Nocturne.Core.Contracts.Timezones;

/// <summary>
/// Manages the current tenant's timezone timeline — the ordered record of which IANA zone the person
/// was in over time. Connectors that store data as local-wall-clock-stamped-as-UTC ("fake UTC", e.g.
/// Glooko) read this timeline to convert timestamps to true UTC at ingest, honouring daylight saving
/// and travel/relocation.
/// </summary>
/// <remarks>
/// The timeline is tenant-scoped (a person fact), not subject- or connector-scoped, and is enforced by
/// RLS on the underlying table. Reads work without an HTTP context, so background connector syncs can
/// use it.
/// </remarks>
/// <seealso cref="TimezoneTimeline"/>
public interface ITimezoneTimelineService
{
    /// <summary>Returns the current tenant's timeline entries, ordered by <c>EffectiveFrom</c> ascending.</summary>
    Task<IReadOnlyList<TimezoneTimelineEntry>> GetTimelineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the tenant's timeline as a ready-to-use resolver.
    /// </summary>
    /// <param name="fallbackOffsetHours">
    /// Legacy fixed offset (hours east of UTC) applied when the timeline is empty or a timestamp predates
    /// the earliest entry — preserves a connector's pre-timeline behaviour so un-seeded tenants don't regress.
    /// </param>
    Task<TimezoneTimeline> GetResolverAsync(double? fallbackOffsetHours, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds the origin entry (covering all history) from the given IANA zone when the tenant has no
    /// timeline yet. Idempotent: does nothing if any entry already exists or the zone is blank.
    /// </summary>
    /// <returns>True if an origin entry was created.</returns>
    Task<bool> EnsureOriginAsync(string ianaTimezone, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a timeline entry for the current tenant.</summary>
    Task<TimezoneTimelineEntry> UpsertAsync(TimezoneTimelineEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Deletes a timeline entry by id. Returns false if no such entry exists for the tenant.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
