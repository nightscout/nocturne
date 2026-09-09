using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.Queries;
namespace Nocturne.API.Services.Entries;

/// <summary>
/// Read-only <see cref="IEntryStore"/> that queries V4 repositories exclusively and projects
/// results into legacy <see cref="Entry"/> shape via <see cref="EntryProjection"/>.
/// Sgv reads serve the canonical glucose stream: legacy clients get one coherent series even
/// when multiple CGMs report concurrently.
/// </summary>
public class EntryReadService : IEntryStore
{
    /// <summary>
    /// Canonical selection drops losing-stream rows after the DB query, so limit-based sgv
    /// fetches over-fetch by this factor before selection to keep pages filled.
    /// </summary>
    private const int CanonicalOverFetchFactor = 3;

    private readonly ISensorGlucoseRepository _sgRepo;
    private readonly IMeterGlucoseRepository _mgRepo;
    private readonly ICalibrationRepository _calRepo;
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly IDemoModeService _demoMode;
    private readonly ILogger<EntryReadService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntryReadService"/>.
    /// </summary>
    public EntryReadService(
        ISensorGlucoseRepository sgRepo,
        IMeterGlucoseRepository mgRepo,
        ICalibrationRepository calRepo,
        ICanonicalGlucoseService canonicalGlucose,
        IDemoModeService demoMode,
        ILogger<EntryReadService> logger)
    {
        _sgRepo = sgRepo;
        _mgRepo = mgRepo;
        _calRepo = calRepo;
        _canonicalGlucose = canonicalGlucose;
        _demoMode = demoMode;
        _logger = logger;
    }

    /// <summary>
    /// Upper bound on rows fetched into memory when a find query carries field filters, which can
    /// only be applied after projection and therefore defeat limit pushdown.
    /// </summary>
    private const int MaxFilterFetch = 100_000;

    /// <summary>
    /// Widest time span one batch duplicate query may cover, and so the bound on the stored
    /// readings it pulls into memory: seven days of one tenant's ingest.
    /// </summary>
    private static readonly TimeSpan MaxProbeChunkSpan = TimeSpan.FromDays(7);

    /// <summary>
    /// Time span each entry in a batch earns towards its chunk. It stops a two-entry batch a week
    /// apart from reading a week of readings: a chunk only widens when enough entries share it.
    /// </summary>
    private static readonly TimeSpan ProbeChunkSpanPerEntry = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entry>> QueryAsync(EntryQuery query, CancellationToken ct = default)
    {
        var descending = !query.ReverseResults;
        var (source, excludeDemo) = ResolveDemoFilter();
        var find = FindQuery.Parse(query.Find);
        var (from, to) = ResolveTimeRange(query, find);

        // A find[type]=x equality routes like an explicit type so the fetch stays single-repo
        var type = !string.IsNullOrEmpty(query.Type) ? query.Type : find.GetEqualityValue("type");

        if (find.HasFieldFiltersExcept("type"))
            return await QueryFilteredAsync(find, type, from, to, source, excludeDemo, query.Count, query.Skip, descending, ct);

        return await QueryByTypeAsync(type, from, to, source, excludeDemo, query.Count, query.Skip, descending, ct);
    }

    private async Task<IReadOnlyList<Entry>> QueryByTypeAsync(
        string? type, DateTime? from, DateTime? to, string? source, bool excludeDemo,
        int count, int skip, bool descending, CancellationToken ct)
    {
        return type switch
        {
            "sgv" => await QuerySgvAsync(from, to, source, excludeDemo, count, skip, descending, ct),
            "mbg" => await QueryMbgAsync(from, to, source, excludeDemo, count, skip, descending, ct),
            "cal" => await QueryCalAsync(from, to, source, excludeDemo, count, skip, descending, ct),
            null or "" => await QueryAllTypesAsync(from, to, source, excludeDemo, count, skip, descending, ct),
            _ => [],
        };
    }

    /// <summary>
    /// Serves a find query with field filters (type $ne, sgv/device/direction conditions, …) by
    /// matching the projected legacy shape. Paging cannot be pushed down past an in-memory
    /// filter, so the fetch window grows geometrically until the page fills or is exhausted.
    /// </summary>
    private async Task<IReadOnlyList<Entry>> QueryFilteredAsync(
        FindQuery find, string? type, DateTime? from, DateTime? to, string? source, bool excludeDemo,
        int count, int skip, bool descending, CancellationToken ct)
    {
        var needed = (long)count + skip;
        var fetchLimit = (int)Math.Min(Math.Max(needed * 4, 100), MaxFilterFetch);

        while (true)
        {
            var page = await QueryByTypeAsync(type, from, to, source, excludeDemo, fetchLimit, 0, descending, ct);
            var matching = page.Where(find.Matches).ToList();
            var exhausted = page.Count < fetchLimit || fetchLimit >= MaxFilterFetch;
            if (matching.Count >= needed || exhausted)
            {
                if (matching.Count < needed && page.Count >= fetchLimit)
                    _logger.LogWarning(
                        "Find-filtered entry query hit the {MaxFetch}-row window; older matches are not returned",
                        MaxFilterFetch);

                return matching.Skip(skip).Take(count).ToList();
            }

            fetchLimit = (int)Math.Min((long)fetchLimit * 4, MaxFilterFetch);
        }
    }

    /// <inheritdoc />
    public async Task<Entry?> GetCurrentAsync(CancellationToken ct = default)
    {
        var (source, excludeDemo) = ResolveDemoFilter();

        // Over-fetch to survive demo filtering and canonical selection dropping the newest rows
        // when a losing stream reported last — a 1-minute losing cadence can outnumber the
        // winner five to one on a descending page.
        const int fetchLimit = 60;
        var results = await _sgRepo.GetAsync(
            from: null, to: null, device: null, source: source,
            limit: fetchLimit, offset: 0, descending: true, nativeOnly: false, ct: ct);

        var visible = ExcludeDemoIfNeeded(results, excludeDemo).ToList();
        var canonical = await _canonicalGlucose.SelectAsync(visible, ct);
        var sg = canonical.FirstOrDefault();
        return sg is null ? null : EntryProjection.FromSensorGlucose(sg);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (Guid.TryParse(id, out var guid))
            return await GetByGuidAsync(guid, ct);

        // A non-UUID id is either a legacy/AAPS-supplied ObjectId (stored as LegacyId) or a 24-hex
        // ObjectId we derived from the record's UUID; resolve the latter via its uuid prefix range.
        var byLegacy = await GetByLegacyIdAsync(id, ct);
        if (byLegacy != null)
            return byLegacy;

        if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
            return await GetByGuidRangeAsync(low, high, ct);

        return null;
    }

    /// <inheritdoc />
    public async Task<Entry?> CheckDuplicateAsync(string? device, string type, double? sgv, long mills,
        int windowMinutes = 5, CancellationToken ct = default)
    {
        var windowMs = (long)windowMinutes * 60 * 1000;
        var from = MillsToUtc(mills - windowMs);
        var to = MillsToUtc(mills + windowMs);

        return type switch
        {
            "sgv" => await CheckSgvDuplicateAsync(device, sgv, from, to, ct),
            "mbg" => await CheckMbgDuplicateAsync(device, sgv, from, to, ct),
            "cal" => await CheckCalDuplicateAsync(device, from, to, ct),
            _ => null,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entry?>> CheckDuplicatesAsync(
        IReadOnlyList<EntryDuplicateProbe> probes, int windowMinutes = 5, CancellationToken ct = default)
    {
        var results = new Entry?[probes.Count];
        var windowMs = (long)windowMinutes * 60 * 1000;

        foreach (var group in probes
            .Select((probe, index) => (probe, index))
            .GroupBy(x => x.probe.Type, StringComparer.Ordinal))
        {
            // Unknown types are never duplicates (CheckDuplicateAsync's default arm) and cost no query.
            if (group.Key is not ("sgv" or "mbg" or "cal"))
                continue;

            foreach (var chunk in ChunkByTimeSpan(group))
                await ClassifyChunkAsync(group.Key, chunk, windowMs, results, ct);
        }

        return results;
    }

    /// <summary>
    /// Splits one type's probes into time-contiguous chunks, each of which becomes a single query.
    /// A chunk widens only while its entries pay for the span (<see cref="ProbeChunkSpanPerEntry"/>
    /// each, never past <see cref="MaxProbeChunkSpan"/>), so the rows a chunk reads stay
    /// proportional to the entries it classifies. In the worst case there is one chunk per probe —
    /// the per-entry probing this replaces — and for an ordinary CGM backlog there is exactly one.
    /// </summary>
    private static List<List<(EntryDuplicateProbe probe, int index)>> ChunkByTimeSpan(
        IEnumerable<(EntryDuplicateProbe probe, int index)> items)
    {
        var chunks = new List<List<(EntryDuplicateProbe probe, int index)>>();
        var current = new List<(EntryDuplicateProbe probe, int index)>();
        var chunkStart = 0L;

        foreach (var item in items.OrderBy(x => x.probe.Mills))
        {
            if (current.Count == 0)
            {
                chunkStart = item.probe.Mills;
            }
            else
            {
                var span = TimeSpan.FromMilliseconds(item.probe.Mills - chunkStart);
                var budget = ProbeChunkSpanPerEntry * current.Count;
                if (budget > MaxProbeChunkSpan)
                    budget = MaxProbeChunkSpan;

                if (span > budget)
                {
                    chunks.Add(current);
                    current = [];
                    chunkStart = item.probe.Mills;
                }
            }

            current.Add(item);
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(string? find = null, string? type = null, CancellationToken ct = default)
    {
        var findQuery = FindQuery.Parse(find);
        var (from, to) = ResolveTimeRange(new EntryQuery { Find = find }, findQuery);
        var effectiveType = !string.IsNullOrEmpty(type) ? type : findQuery.GetEqualityValue("type");

        if (findQuery.HasFieldFiltersExcept("type"))
        {
            // Field filters only exist on the projected shape; count matches within the
            // (bounded) window instead of delegating to per-repo counts.
            var (source, excludeDemo) = ResolveDemoFilter();
            var page = await QueryByTypeAsync(
                effectiveType, from, to, source, excludeDemo, MaxFilterFetch, 0, descending: true, ct);
            return page.Count(findQuery.Matches);
        }

        return effectiveType switch
        {
            "sgv" => await _sgRepo.CountAsync(from, to, ct),
            "mbg" => await _mgRepo.CountAsync(from, to, ct),
            "cal" => await _calRepo.CountAsync(from, to, ct),
            null or "" => await CountAllTypesAsync(from, to, ct),
            _ => 0,
        };
    }

    private async Task<long> CountAllTypesAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var sgCount = await _sgRepo.CountAsync(from, to, ct);
        var mgCount = await _mgRepo.CountAsync(from, to, ct);
        var calCount = await _calRepo.CountAsync(from, to, ct);
        return sgCount + mgCount + calCount;
    }

    #region Private — Query helpers

    private async Task<IReadOnlyList<Entry>> QuerySgvAsync(
        DateTime? from, DateTime? to, string? source, bool excludeDemo,
        int count, int skip, bool descending, CancellationToken ct)
    {
        // Canonical selection happens after the DB query, so paging cannot be pushed down:
        // over-fetch from offset 0, select, then page.
        var canonical = await FetchCanonicalSgvAsync(from, to, source, excludeDemo, (long)count + skip, descending, ct);
        return canonical.Skip(skip).Take(count).Select(EntryProjection.FromSensorGlucose).ToList();
    }

    /// <summary>
    /// Fetches sgv readings with demo filtering and canonical stream selection applied,
    /// over-fetching so at least <paramref name="needed"/> canonical rows survive when a
    /// losing stream contributed to the raw page. A losing stream can outnumber the winner by
    /// cadence (1-minute vs 5-minute), so the fetch grows geometrically until the page fills
    /// or the raw window is exhausted.
    /// </summary>
    private async Task<IReadOnlyList<Core.Models.V4.SensorGlucose>> FetchCanonicalSgvAsync(
        DateTime? from, DateTime? to, string? source, bool excludeDemo,
        long needed, bool descending, CancellationToken ct)
    {
        const int maxFetch = 100_000;
        var target = Math.Max(1, needed);
        var fetchCount = (int)Math.Min(target * CanonicalOverFetchFactor, maxFetch);

        while (true)
        {
            var results = (await _sgRepo.GetAsync(from, to, device: null, source, fetchCount, 0, descending, false, null, null, ct)).ToList();
            var visible = ExcludeDemoIfNeeded(results, excludeDemo).ToList();
            var canonical = await _canonicalGlucose.SelectAsync(visible, ct);

            var exhausted = results.Count < fetchCount || fetchCount >= maxFetch;
            if (canonical.Count >= target || exhausted)
                return canonical;

            fetchCount = (int)Math.Min((long)fetchCount * CanonicalOverFetchFactor, maxFetch);
        }
    }

    private async Task<IReadOnlyList<Entry>> QueryMbgAsync(
        DateTime? from, DateTime? to, string? source, bool excludeDemo,
        int count, int skip, bool descending, CancellationToken ct)
    {
        // Single-type query: push limit/offset directly to the database
        var results = await _mgRepo.GetAsync(from, to, device: null, source, count, skip, descending, ct);
        return ExcludeDemoIfNeeded(results, excludeDemo).Select(EntryProjection.FromMeterGlucose).ToList();
    }

    private async Task<IReadOnlyList<Entry>> QueryCalAsync(
        DateTime? from, DateTime? to, string? source, bool excludeDemo,
        int count, int skip, bool descending, CancellationToken ct)
    {
        // Single-type query: push limit/offset directly to the database
        var results = await _calRepo.GetAsync(from, to, device: null, source, count, skip, descending, ct);
        return ExcludeDemoIfNeeded(results, excludeDemo).Select(EntryProjection.FromCalibration).ToList();
    }

    private async Task<IReadOnlyList<Entry>> QueryAllTypesAsync(
        DateTime? from, DateTime? to, string? source, bool excludeDemo,
        int count, int skip, bool descending, CancellationToken ct)
    {
        // Multi-type merge requires over-fetching because we interleave across repos before paginating
        var fetchCount = (int)Math.Min((long)count + skip, 100_000);

        // Sequential to avoid DbContext thread-safety issues with scoped lifetime
        var sgResults = await FetchCanonicalSgvAsync(from, to, source, excludeDemo, (long)fetchCount, descending, ct);
        var mgResults = await _mgRepo.GetAsync(from, to, device: null, source, fetchCount, 0, descending, ct);
        var calResults = await _calRepo.GetAsync(from, to, device: null, source, fetchCount, 0, descending, ct);

        var entries = sgResults.Select(EntryProjection.FromSensorGlucose)
            .Concat(ExcludeDemoIfNeeded(mgResults, excludeDemo).Select(EntryProjection.FromMeterGlucose))
            .Concat(ExcludeDemoIfNeeded(calResults, excludeDemo).Select(EntryProjection.FromCalibration));

        var sorted = descending
            ? entries.OrderByDescending(e => e.Mills)
            : entries.OrderBy(e => e.Mills);

        return sorted.Skip(skip).Take(count).ToList();
    }

    #endregion

    #region Private — GetById helpers

    private async Task<Entry?> GetByGuidAsync(Guid id, CancellationToken ct)
    {
        var sg = await _sgRepo.GetByIdAsync(id, ct);
        if (sg is not null)
            return EntryProjection.FromSensorGlucose(sg);

        var mg = await _mgRepo.GetByIdAsync(id, ct);
        if (mg is not null)
            return EntryProjection.FromMeterGlucose(mg);

        var cal = await _calRepo.GetByIdAsync(id, ct);
        if (cal is not null)
            return EntryProjection.FromCalibration(cal);

        return null;
    }

    private async Task<Entry?> GetByLegacyIdAsync(string legacyId, CancellationToken ct)
    {
        var sg = await _sgRepo.GetByLegacyIdAsync(legacyId, ct);
        if (sg is not null)
            return EntryProjection.FromSensorGlucose(sg);

        var mg = await _mgRepo.GetByLegacyIdAsync(legacyId, ct);
        if (mg is not null)
            return EntryProjection.FromMeterGlucose(mg);

        var cal = await _calRepo.GetByLegacyIdAsync(legacyId, ct);
        if (cal is not null)
            return EntryProjection.FromCalibration(cal);

        return null;
    }

    private async Task<Entry?> GetByGuidRangeAsync(Guid low, Guid high, CancellationToken ct)
    {
        var sg = await _sgRepo.GetByGuidRangeAsync(low, high, ct);
        if (sg is not null)
            return EntryProjection.FromSensorGlucose(sg);

        var mg = await _mgRepo.GetByGuidRangeAsync(low, high, ct);
        if (mg is not null)
            return EntryProjection.FromMeterGlucose(mg);

        var cal = await _calRepo.GetByGuidRangeAsync(low, high, ct);
        if (cal is not null)
            return EntryProjection.FromCalibration(cal);

        return null;
    }

    #endregion

    #region Private — Duplicate check helpers

    private async Task<Entry?> CheckSgvDuplicateAsync(
        string? device, double? sgv, DateTime from, DateTime to, CancellationToken ct)
    {
        // Probe raw storage rather than the visibility-filtered GetAsync: copies linked as
        // non-primary cross-connector duplicates are hidden from reads, but they still mean the
        // reading is already stored — a filtered check re-inserts them on every upload.
        var match = await _sgRepo.FindStoredDuplicateAsync(device, sgv, from, to, ct);
        return match is null ? null : EntryProjection.FromSensorGlucose(match);
    }

    private async Task<Entry?> CheckMbgDuplicateAsync(
        string? device, double? mbg, DateTime from, DateTime to, CancellationToken ct)
    {
        var results = await _mgRepo.GetAsync(from, to, device, source: null, limit: 100, offset: 0, descending: true, ct: ct);
        var match = mbg.HasValue
            ? results.FirstOrDefault(r => Math.Abs(r.Mgdl - mbg.Value) < 0.01)
            : results.FirstOrDefault();
        return match is null ? null : EntryProjection.FromMeterGlucose(match);
    }

    private async Task<Entry?> CheckCalDuplicateAsync(
        string? device, DateTime from, DateTime to, CancellationToken ct)
    {
        var results = await _calRepo.GetAsync(from, to, device, source: null, limit: 100, offset: 0, descending: true, ct: ct);
        var match = results.FirstOrDefault();
        return match is null ? null : EntryProjection.FromCalibration(match);
    }

    /// <summary>
    /// Loads one chunk's stored readings in a single query and classifies every probe in it.
    /// <paramref name="chunk"/> is ordered by timestamp, so its ends give the query's window.
    /// </summary>
    private async Task ClassifyChunkAsync(
        string type,
        List<(EntryDuplicateProbe probe, int index)> chunk,
        long windowMs,
        Entry?[] results,
        CancellationToken ct)
    {
        var from = MillsToUtc(chunk[0].probe.Mills - windowMs);
        var to = MillsToUtc(chunk[^1].probe.Mills + windowMs);

        switch (type)
        {
            case "sgv":
            {
                var candidates = await _sgRepo.FindStoredDuplicateCandidatesAsync(
                    ResolveProbeDevices(chunk), from, to, ct);
                Classify(chunk, candidates, windowMs, results,
                    c => c.Timestamp, c => c.Device, c => c.Mgdl, EntryProjection.FromSensorGlucose);
                return;
            }
            case "mbg":
            {
                // Unpaged, and across every device: a probe without a device matches a reading
                // from any device, and the chunk's span is what keeps the row count bounded.
                var candidates = (await _mgRepo.GetAsync(
                    from, to, device: null, source: null, limit: int.MaxValue, offset: 0,
                    descending: true, ct: ct)).ToList();
                Classify(chunk, candidates, windowMs, results,
                    c => c.Timestamp, c => c.Device, c => c.Mgdl, EntryProjection.FromMeterGlucose);
                return;
            }
            default:
            {
                var candidates = (await _calRepo.GetAsync(
                    from, to, device: null, source: null, limit: int.MaxValue, offset: 0,
                    descending: true, ct: ct)).ToList();
                // Calibrations carry no comparable value: the single-entry probe matches on
                // device and window alone, so no value selector is supplied.
                Classify(chunk, candidates, windowMs, results,
                    c => c.Timestamp, c => c.Device, valueOf: null, EntryProjection.FromCalibration);
                return;
            }
        }
    }

    private static void Classify<TRecord>(
        List<(EntryDuplicateProbe probe, int index)> items,
        IReadOnlyList<TRecord> candidates,
        long windowMs,
        Entry?[] results,
        Func<TRecord, DateTime> timestampOf,
        Func<TRecord, string?> deviceOf,
        Func<TRecord, double>? valueOf,
        Func<TRecord, Entry> project)
        where TRecord : class
    {
        foreach (var (probe, index) in items)
        {
            var match = MatchInWindow(candidates, probe, windowMs, timestampOf, deviceOf, valueOf);
            results[index] = match is null ? null : project(match);
        }
    }

    /// <summary>
    /// The single-entry probe's match rule, applied in memory: the first candidate inside the
    /// probe's own window whose device and value match. <paramref name="candidates"/> arrive
    /// newest-first in the order the per-entry query resolved ties by, so the first match is the
    /// same row it returned.
    /// </summary>
    private static TRecord? MatchInWindow<TRecord>(
        IReadOnlyList<TRecord> candidates,
        EntryDuplicateProbe probe,
        long windowMs,
        Func<TRecord, DateTime> timestampOf,
        Func<TRecord, string?> deviceOf,
        Func<TRecord, double>? valueOf)
        where TRecord : class
    {
        var from = MillsToUtc(probe.Mills - windowMs);
        var to = MillsToUtc(probe.Mills + windowMs);

        foreach (var candidate in candidates)
        {
            var timestamp = timestampOf(candidate);
            if (timestamp < from || timestamp > to)
                continue;
            if (probe.Device is not null && !string.Equals(deviceOf(candidate), probe.Device, StringComparison.Ordinal))
                continue;
            if (valueOf is not null && probe.Sgv.HasValue && Math.Abs(valueOf(candidate) - probe.Sgv.Value) >= 0.01)
                continue;
            return candidate;
        }

        return null;
    }

    /// <summary>
    /// The device filter for a chunk's fetch: <c>null</c> (every device) when any probe has no
    /// device, because such a probe matches a stored reading from any device.
    /// </summary>
    private static IReadOnlyCollection<string>? ResolveProbeDevices(
        List<(EntryDuplicateProbe probe, int index)> items)
    {
        var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (probe, _) in items)
        {
            if (probe.Device is null)
                return null;
            devices.Add(probe.Device);
        }
        return devices;
    }

    private static DateTime MillsToUtc(long mills) =>
        DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

    #endregion

    #region Private — Filter resolution

    /// <summary>
    /// Resolves the demo mode source filter. When demo mode is enabled, returns the demo source
    /// to positively filter for demo data. When disabled, returns <c>null</c> (no source filter)
    /// and sets <c>excludeDemo</c> to <c>true</c> so callers post-filter demo records out.
    /// </summary>
    /// <remarks>
    /// The V4 repositories only support exact-match source filtering, not negation,
    /// so we post-filter demo records out instead.
    /// </remarks>
    private (string? Source, bool ExcludeDemo) ResolveDemoFilter()
    {
        if (_demoMode.IsEnabled)
            return (DataSources.DemoService, false);

        // Demo mode off: no source filter, but exclude demo rows after fetch
        return (null, true);
    }

    /// <summary>
    /// Filters out ephemeral (demo/test) records when <paramref name="exclude"/> is <c>true</c>.
    /// Returns the sequence unchanged when filtering is not needed.
    /// </summary>
    private static IEnumerable<T> ExcludeDemoIfNeeded<T>(IEnumerable<T> results, bool exclude)
        where T : Core.Models.V4.IV4Record
    {
        return exclude
            ? results.Where(r => !DataSources.IsEphemeral(r.DataSource))
            : results;
    }

    private static (DateTime? From, DateTime? To) ResolveTimeRange(EntryQuery query, FindQuery find)
    {
        DateTime? from = null;
        DateTime? to = null;

        // Time range from the parsed find query
        var (fromMills, toMills) = (find.FromMills, find.ToMills);
        if (fromMills.HasValue)
            from = DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime;
        if (toMills.HasValue)
            to = DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime;

        // DateString takes priority over Find-based time range. Both cannot be combined because
        // the V4 repos accept a single from/to window; DateString wins when both are present.
        if (query.DateString is not null && DateTime.TryParse(query.DateString, out var parsedDate))
        {
            from = parsedDate.ToUniversalTime();
            to = from.Value.AddDays(1);
        }

        // Explicit FromMills/ToMills win outright — typed callers (alert replay) use these
        // instead of round-tripping through Find or DateString.
        if (query.FromMills.HasValue)
            from = DateTimeOffset.FromUnixTimeMilliseconds(query.FromMills.Value).UtcDateTime;
        if (query.ToMills.HasValue)
            to = DateTimeOffset.FromUnixTimeMilliseconds(query.ToMills.Value).UtcDateTime;

        return (from, to);
    }

    #endregion
}
