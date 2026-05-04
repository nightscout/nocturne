using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Entries;
namespace Nocturne.API.Services.Entries;

/// <summary>
/// Read-only <see cref="IEntryStore"/> that queries V4 repositories exclusively and projects
/// results into legacy <see cref="Entry"/> shape via <see cref="EntryProjection"/>.
/// </summary>
public class EntryReadService : IEntryStore
{
    private readonly ISensorGlucoseRepository _sgRepo;
    private readonly IMeterGlucoseRepository _mgRepo;
    private readonly ICalibrationRepository _calRepo;
    private readonly IDemoModeService _demoMode;
    private readonly ILogger<EntryReadService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntryReadService"/>.
    /// </summary>
    public EntryReadService(
        ISensorGlucoseRepository sgRepo,
        IMeterGlucoseRepository mgRepo,
        ICalibrationRepository calRepo,
        IDemoModeService demoMode,
        ILogger<EntryReadService> logger)
    {
        _sgRepo = sgRepo;
        _mgRepo = mgRepo;
        _calRepo = calRepo;
        _demoMode = demoMode;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entry>> QueryAsync(EntryQuery query, CancellationToken ct = default)
    {
        var descending = !query.ReverseResults;
        var (source, excludeDemo) = ResolveDemoFilter();
        var (from, to) = ResolveTimeRange(query);

        return query.Type switch
        {
            "sgv" => await QuerySgvAsync(from, to, source, excludeDemo, query.Count, query.Skip, descending, ct),
            "mbg" => await QueryMbgAsync(from, to, source, excludeDemo, query.Count, query.Skip, descending, ct),
            "cal" => await QueryCalAsync(from, to, source, excludeDemo, query.Count, query.Skip, descending, ct),
            null or "" => await QueryAllTypesAsync(from, to, source, excludeDemo, query.Count, query.Skip, descending, ct),
            _ => [],
        };
    }

    /// <inheritdoc />
    public async Task<Entry?> GetCurrentAsync(CancellationToken ct = default)
    {
        var (source, excludeDemo) = ResolveDemoFilter();

        // When excluding demo data, over-fetch to account for filtered-out demo rows
        var fetchLimit = excludeDemo ? 10 : 1;
        var results = await _sgRepo.GetAsync(
            from: null, to: null, device: null, source: source,
            limit: fetchLimit, offset: 0, descending: true, nativeOnly: false, ct: ct);

        var sg = excludeDemo
            ? results.FirstOrDefault(r => !DataSources.IsEphemeral(r.DataSource))
            : results.FirstOrDefault();
        return sg is null ? null : EntryProjection.FromSensorGlucose(sg);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (Guid.TryParse(id, out var guid))
            return await GetByGuidAsync(guid, ct);

        return await GetByLegacyIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<Entry?> CheckDuplicateAsync(string? device, string type, double? sgv, long mills,
        int windowMinutes = 5, CancellationToken ct = default)
    {
        var windowMs = (long)windowMinutes * 60 * 1000;
        var from = DateTimeOffset.FromUnixTimeMilliseconds(mills - windowMs).UtcDateTime;
        var to = DateTimeOffset.FromUnixTimeMilliseconds(mills + windowMs).UtcDateTime;

        return type switch
        {
            "sgv" => await CheckSgvDuplicateAsync(device, sgv, from, to, ct),
            "mbg" => await CheckMbgDuplicateAsync(device, sgv, from, to, ct),
            "cal" => await CheckCalDuplicateAsync(device, from, to, ct),
            _ => null,
        };
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(string? find = null, string? type = null, CancellationToken ct = default)
    {
        var (from, to) = ResolveTimeRange(new EntryQuery { Find = find });

        return type switch
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
        // Single-type query: push limit/offset directly to the database
        var results = await _sgRepo.GetAsync(from, to, device: null, source, count, skip, descending, false, ct);
        return ExcludeDemoIfNeeded(results, excludeDemo).Select(EntryProjection.FromSensorGlucose).ToList();
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
        var fetchCount = count + skip;

        // Sequential to avoid DbContext thread-safety issues with scoped lifetime
        var sgResults = await _sgRepo.GetAsync(from, to, device: null, source, fetchCount, 0, descending, false, ct);
        var mgResults = await _mgRepo.GetAsync(from, to, device: null, source, fetchCount, 0, descending, ct);
        var calResults = await _calRepo.GetAsync(from, to, device: null, source, fetchCount, 0, descending, ct);

        var entries = ExcludeDemoIfNeeded(sgResults, excludeDemo).Select(EntryProjection.FromSensorGlucose)
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

    #endregion

    #region Private — Duplicate check helpers

    private async Task<Entry?> CheckSgvDuplicateAsync(
        string? device, double? sgv, DateTime from, DateTime to, CancellationToken ct)
    {
        var results = await _sgRepo.GetAsync(from, to, device, source: null, limit: 100, offset: 0, descending: true, nativeOnly: false, ct: ct);
        var match = sgv.HasValue
            ? results.FirstOrDefault(r => Math.Abs(r.Mgdl - sgv.Value) < 0.01)
            : results.FirstOrDefault();
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

    private static (DateTime? From, DateTime? To) ResolveTimeRange(EntryQuery query)
    {
        DateTime? from = null;
        DateTime? to = null;

        // Parse time range from MongoDB-style find query
        var (fromMills, toMills) = EntryDomainLogic.ParseTimeRangeFromFind(query.Find);
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
