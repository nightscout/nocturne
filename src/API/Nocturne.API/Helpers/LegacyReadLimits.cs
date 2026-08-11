namespace Nocturne.API.Helpers;

/// <summary>
/// Server-side ceilings on how many records a legacy v1 read may return.
/// </summary>
/// <remarks>
/// <para>
/// The v1 <c>count</c> query parameter is 1:1 Nightscout-compatible, and Nightscout applies no
/// upper bound of its own — a single request can ask for an arbitrary number of records. On a
/// shared multi-tenant deployment that is a cost-amplification vector, so the ceilings here are
/// applied independently of each endpoint's compat default.
/// </para>
/// <para>
/// Both ceilings sit at or above 10,000, which is the largest page any first-party caller asks
/// for: the migration job pages at 10,000 and the Nightscout connector's configurable page size
/// is attribute-capped at 10,000.
/// </para>
/// <para>
/// Not every v1 read needs a ceiling here. <c>times/</c> and <c>slice/</c> take a <c>count</c> but
/// are already bounded by <c>TimeQueryService</c>, which fetches a fixed 1,000 rows per storage
/// before the controller paginates, so their responses cannot exceed that regardless of
/// <c>count</c>. <c>pebble</c> (10) and <c>profile</c> (1,000) carry their own tighter clamps, and
/// every v3 read clamps in <c>BaseV3Controller.ParseV3QueryParameters</c> or inline on the
/// <c>history/</c> routes.
/// </para>
/// <para>
/// Only the upper bound lives here. Zero and negative counts stay with each route, because
/// Nightscout's behaviour differs per route: entries, treatments, devicestatus and activity answer
/// an empty array, while <c>times/</c> and <c>slice/</c> answer 400.
/// </para>
/// <para>
/// <c>MaxCount</c> shares a value with <c>EntryReadService.MaxFilterFetch</c> and
/// <c>ActivityService.MaxOverFetch</c> by coincidence, not by coupling. Those constants bound rows
/// pulled into memory to satisfy a request; these bound what a caller may ask to be returned. Each
/// may move alone, so nothing here is referenced from a service's own internal over-fetch bound.
/// </para>
/// </remarks>
public static class LegacyReadLimits
{
    /// <summary>
    /// Maximum number of records returned by a v1 read that projects one stored row into one
    /// response record — entries and treatments.
    /// </summary>
    /// <remarks>
    /// Sits above every known caller: uploaders and followers (AAPS, xDrip, Trio) request tens to
    /// hundreds, the Nightscout migration connector pages at 1,000, and report exports ask for tens
    /// of thousands. A tenant's full multi-year entry history is on the order of a few hundred
    /// thousand records, so this bounds a single response to a fraction of it while leaving
    /// legitimate bulk reads untouched.
    /// </remarks>
    public const int MaxCount = 100_000;

    /// <summary>
    /// Maximum number of records returned by a v1 read that merges several storages into each
    /// response record — activity and devicestatus.
    /// </summary>
    /// <remarks>
    /// An order of magnitude below <see cref="MaxCount"/> because <c>count</c> multiplies at these
    /// routes rather than tracking the row count. <c>ActivityService</c> issues four independent
    /// fetches of <c>count</c> records each (state spans, heart rate, step count, sleep) and merges
    /// them in memory, so the objects materialized are a multiple of what is returned; at 1 Hz,
    /// heart-rate data alone reaches 10,000 rows in under three hours.
    /// <c>DeviceStatusProjectionService</c> runs two windowed queries plus three correlation-id
    /// batch loads and an overlapping-override scan, then emits a composite document carrying the
    /// APS, pump and uploader payloads. Ten thousand still clears the 10,000-record page every
    /// first-party bulk caller uses.
    /// </remarks>
    /// <remarks>
    /// Governs the v1 routes only. <c>/api/v4/activity</c> reaches the same four-source fan-out
    /// with no ceiling of its own; capping it is queued separately, because v4 should not import a
    /// legacy-compat helper.
    /// </remarks>
    public const int MaxMergedCount = 10_000;

    /// <summary>
    /// The number of records a merged v1 read may return for a page starting at
    /// <paramref name="skip"/>, so that <c>skip</c> cannot walk past the budget
    /// <see cref="MaxMergedCount"/> sets.
    /// </summary>
    /// <remarks>
    /// Activity merges its four sources in memory and therefore over-fetches <c>count + skip</c>
    /// records from each, so clamping <c>count</c> alone leaves the cost proportional to <c>skip</c>
    /// — <c>?count=10000&amp;skip=90000</c> would still materialize ten times the intended budget.
    /// Bounding the window instead means the merged set is reachable only to its first
    /// <see cref="MaxMergedCount"/> records and deeper pages come back empty. No first-party caller
    /// pages these routes with <c>skip</c> at all: the migration job and the Nightscout connector
    /// both page by time cursor.
    /// </remarks>
    /// <param name="count">The requested record count.</param>
    /// <param name="skip">The requested offset, already normalized to be non-negative.</param>
    /// <returns>
    /// The records this page may return: <paramref name="count"/> clamped to whatever remains of
    /// the window, and zero once <paramref name="skip"/> reaches <see cref="MaxMergedCount"/>.
    /// </returns>
    public static int ClampMergedPage(int count, int skip) =>
        Math.Min(count, MaxMergedCount - Math.Min(skip, MaxMergedCount));

    /// <summary>
    /// Clamp a caller-supplied v1 <c>count</c> to <see cref="MaxCount"/>.
    /// </summary>
    /// <param name="count">The requested record count.</param>
    /// <returns><paramref name="count"/>, or <see cref="MaxCount"/> if it exceeds the ceiling.</returns>
    public static int ClampCount(int count) => Math.Min(count, MaxCount);

    /// <summary>
    /// Clamp a caller-supplied v1 <c>count</c> to <see cref="MaxMergedCount"/>.
    /// </summary>
    /// <param name="count">The requested record count.</param>
    /// <returns><paramref name="count"/>, or <see cref="MaxMergedCount"/> if it exceeds the ceiling.</returns>
    public static int ClampMergedCount(int count) => Math.Min(count, MaxMergedCount);
}
