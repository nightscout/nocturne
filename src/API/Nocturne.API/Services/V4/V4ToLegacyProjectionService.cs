using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Projects V4 granular records back into the legacy <see cref="Entry"/> and <see cref="Treatment"/>
/// shapes for v1/v2/v3 API compatibility.
/// </summary>
/// <remarks>
/// This service is the read side of the dual-path architecture.
/// The write side (legacy record → V4 typed record) is handled by <see cref="DecompositionPipeline"/>.
/// Projection covers: <see cref="SensorGlucose"/> → <see cref="Entry"/>,
/// <see cref="Bolus"/> and <see cref="CarbIntake"/> → <see cref="Treatment"/>,
/// <see cref="DeviceEvent"/> → <see cref="Treatment"/> using the legacy event-type map, and the
/// <see cref="StateSpan"/>s a legacy treatment was decomposed into back into that treatment.
/// <para>
/// Entries are projected V4-native-only, supplementing the rows the legacy entries table still
/// holds. Treatments have no legacy table left to supplement, so every treatment read projects
/// records of both provenances — see <see cref="LegacyTreatmentRange.NativeOnly"/>.
/// </para>
/// </remarks>
/// <seealso cref="IV4ToLegacyProjectionService"/>
/// <seealso cref="LegacyTreatmentTables"/>
/// <seealso cref="DecompositionPipeline"/>
public class V4ToLegacyProjectionService : IV4ToLegacyProjectionService
{
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly LegacyTreatmentRepositories _repositories;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<V4ToLegacyProjectionService> _logger;

    public V4ToLegacyProjectionService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        ITempBasalRepository tempBasalRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITreatmentFoodService treatmentFoodService,
        NocturneDbContext dbContext,
        ILogger<V4ToLegacyProjectionService> logger
    )
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _repositories = new LegacyTreatmentRepositories(
            bolusRepository,
            carbIntakeRepository,
            bgCheckRepository,
            noteRepository,
            deviceEventRepository,
            tempBasalRepository,
            bolusCalculationRepository
        );
        _treatmentFoodService = treatmentFoodService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <summary>
    /// Converts nullable unix milliseconds to nullable DateTime.
    /// </summary>
    private static DateTime? MillsToDateTime(long? mills) =>
        mills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value).UtcDateTime : null;

    public async Task<IEnumerable<Entry>> GetProjectedEntriesAsync(
        long? fromMills,
        long? toMills,
        int limit,
        int offset,
        bool descending,
        CancellationToken ct = default
    )
    {
        IEnumerable<SensorGlucose> records;
        try
        {
            records = await _sensorGlucoseRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: offset,
                descending: descending,
                nativeOnly: true,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 SensorGlucose records for projection");
            return Enumerable.Empty<Entry>();
        }

        return records.Select(ProjectSensorGlucoseToEntry);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetLatestProjectedEntryAsync(CancellationToken ct = default)
    {
        IEnumerable<SensorGlucose> records;
        try
        {
            records = await _sensorGlucoseRepository.GetAsync(
                from: null,
                to: null,
                device: null,
                source: null,
                limit: 1,
                offset: 0,
                descending: true,
                nativeOnly: true,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 SensorGlucose records for latest projection");
            return null;
        }

        var latest = records.FirstOrDefault();
        return latest == null ? null : ProjectSensorGlucoseToEntry(latest);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        int limit,
        bool nativeOnly = true,
        CancellationToken ct = default
    )
    {
        var range = new LegacyTreatmentRange(
            MillsToDateTime(fromMills), MillsToDateTime(toMills), limit, nativeOnly);

        var rows = await FetchAsync(table => table.InRangeAsync(_repositories, _dbContext, range, ct));
        var treatments = await AssembleAsync(rows, ct);

        return treatments.OrderByDescending(t => t.Mills).Take(limit);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each table pages independently on a millisecond boundary; see <see cref="HistoryPage"/>.
    /// </remarks>
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsModifiedSinceAsync(
        long lastModifiedMills, int limit, CancellationToken ct = default)
    {
        var perTable = new List<(ILegacyTreatmentTable Table, int Order, IReadOnlyList<FetchedRecord> Rows)>();
        foreach (var table in LegacyTreatmentTables.All)
        {
            var rows = await FetchSafe(
                table, t => t.ModifiedSinceAsync(_dbContext, lastModifiedMills, limit, _logger, ct));
            perTable.Add((table, LegacyTreatmentTables.OrderOf(table), rows));
        }

        // The page is cut on raw row stamps and only then paired. Each type contributes its own
        // oldest rows, so this merge holds the globally-oldest `limit`, and every row left behind is
        // strictly newer than every row returned. Pairing before the cut breaks that: a meal carries
        // its newer constituent's stamp, so it can sort past the cut while the cursor (max(srvModified)
        // over the page) still advances beyond the older constituent's own row, which is then never
        // fetched again. Pairing after the cut can only merge rows that were both delivered, so a pair
        // split by the cut simply arrives as two treatments.
        var ordered = perTable
            .SelectMany(p => p.Rows.Select(row => (Row: row, p.Order)))
            .OrderBy(x => x.Row.Modified)
            .ThenBy(x => x.Order)
            .ThenBy(x => RecordId(x.Row))
            .ToList();

        var page = ordered.Take(limit).Select(x => x.Row).ToList();

        if (page.Count > 0)
            page = ExtendToMillisecondAsync(page, ordered);

        page = await WithLiveMealPartnersAsync(page, ct);

        var treatments = await AssembleAsync(page, ct);

        return treatments.OrderBy(t => t.SrvModified ?? t.Mills);
    }

    /// <summary>
    /// Extends a merged page to the end of its last row's millisecond from the rows already fetched.
    /// Every table's own page ends on a millisecond boundary, so the row that set the merged cut
    /// carries its whole tie group into that page; the merge can still cut a group whose members
    /// span tables, which is what this completes.
    /// </summary>
    private static List<FetchedRecord> ExtendToMillisecondAsync(
        List<FetchedRecord> page,
        IReadOnlyList<(FetchedRecord Row, int Order)> ordered)
    {
        var lastMills = HistoryPage.ToMilliseconds(page[^1].Modified);
        var seen = page.Select(RecordId).ToHashSet();

        foreach (var (row, _) in ordered)
        {
            if (HistoryPage.ToMilliseconds(row.Modified) == lastMills
                && seen.Add(RecordId(row)))
            {
                page.Add(row);
            }
        }

        return page
            .OrderBy(r => r.Modified)
            .ThenBy(r => LegacyTreatmentTables.OrderOf(r.Table))
            .ThenBy(RecordId)
            .ToList();
    }

    /// <summary>
    /// Adds to <paramref name="page"/> the live boluses and carb intakes correlated with a deleted one
    /// in it, stamped with that delete. A live meal is served under its bolus id, so deleting one half
    /// changes what the other reads as: a surviving carb intake becomes a standalone treatment under
    /// its own id, a surviving bolus loses its carbs under the meal's id. Neither survivor's own row
    /// moved, so without this the client keeps the meal (carbs deleted) or loses the carbs (bolus
    /// deleted). Stamped at or below the page's newest row, the added rows leave its cursor where it was.
    /// </summary>
    private async Task<List<FetchedRecord>> WithLiveMealPartnersAsync(
        List<FetchedRecord> page, CancellationToken ct)
    {
        var deletes = new Dictionary<Guid, DateTime>();
        foreach (var row in page.Where(r => r.Deleted && r.Record is Bolus or CarbIntake))
        {
            if (((IV4Record)row.Record).CorrelationId is not { } correlationId)
                continue;
            if (!deletes.TryGetValue(correlationId, out var stamp) || row.Modified > stamp)
                deletes[correlationId] = row.Modified;
        }

        if (deletes.Count == 0)
            return page;

        var seen = page.Select(RecordId).ToHashSet();
        var partners = new List<FetchedRecord>();
        foreach (var table in LegacyTreatmentTables.MealTables)
        {
            var live = await FetchSafe(table, t => t.LiveByCorrelationAsync(_dbContext, deletes.Keys, ct));
            partners.AddRange(live
                .Where(row => seen.Add(RecordId(row)))
                .Select(row => row with { Modified = deletes[((IV4Record)row.Record).CorrelationId!.Value] }));
        }

        return [.. page, .. partners];
    }

    private static Guid RecordId(FetchedRecord row) => row.Id;

    /// <inheritdoc />
    /// <remarks>
    /// An id resolves when it is the span's primary key, the 24-hex ObjectId served for that key, or
    /// the treatment id the span was written under, verbatim or as the ObjectId it is served as.
    /// </remarks>
    public async Task<Treatment?> GetProjectedStateSpanTreatmentAsync(string id, CancellationToken ct = default) =>
        await FindStateSpanAsync(id, ct) is { } row
            ? (await AssembleAsync([row], ct)).Single()
            : null;

    /// <inheritdoc />
    /// <remarks>Resolves an id as <see cref="GetProjectedStateSpanTreatmentAsync"/> does.</remarks>
    public async Task<string?> GetStateSpanTreatmentIdAsync(string id, CancellationToken ct = default) =>
        await FindStateSpanAsync(id, ct) is { } row ? ((StateSpanEntity)row.Record).OriginalId : null;

    private async Task<FetchedRecord?> FindStateSpanAsync(string id, CancellationToken ct)
    {
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            var span = await MatchingId(table.Rows(_dbContext), id)
                .OrderBy(s => s.OriginalId == id ? 0 : 1)
                .ThenBy(s => s.Id)
                .FirstOrDefaultAsync(ct);

            if (span is not null)
                return table.Fetched(span);
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>Resolves an id as <see cref="GetProjectedStateSpanTreatmentAsync"/> does.</remarks>
    public async Task<bool> IsStateSpanDeletedByUserAsync(string id, CancellationToken ct = default)
    {
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            if (await MatchingId(table.UserTombstones(_dbContext), id).AnyAsync(ct))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<long> CountProjectedStateSpanTreatmentsAsync(
        long? fromMills, long? toMills, CancellationToken ct = default)
    {
        var (from, to) = (MillsToDateTime(fromMills), MillsToDateTime(toMills));
        long count = 0;
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            count += await FetchSafe(
                table, _ => table.InWindow(_dbContext, from, to).LongCountAsync(ct), 0L);
        }

        var notes = _dbContext.Notes.AsNoTracking().ExcludeNonPrimary(_dbContext, RecordType.Note);
        if (from is { } lower)
            notes = notes.Where(n => n.Timestamp >= lower);
        if (to is { } upper)
            notes = notes.Where(n => n.Timestamp <= upper);

        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            var keys = table.Rows(_dbContext).Select(s => s.OriginalId);
            count -= await FetchSafe(
                table, _ => notes.Where(n => n.LegacyId != null && keys.Contains(n.LegacyId)).LongCountAsync(ct), 0L);
        }

        return count;
    }

    private static IQueryable<StateSpanEntity> MatchingId(IQueryable<StateSpanEntity> rows, string id)
    {
        if (Guid.TryParse(id, out var guid))
        {
            string[] forms = [id, guid.ToString(), guid.ToString().ToUpperInvariant()];
            return rows.Where(s => s.Id == guid || forms.Contains(s.OriginalId!));
        }

        if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            var (lowerFrom, lowerTo) = (low.ToString(), high.ToString());
            var (upperFrom, upperTo) = (lowerFrom.ToUpperInvariant(), lowerTo.ToUpperInvariant());
            return rows.Where(s => s.OriginalId == id
                || (s.Id >= low && s.Id <= high)
                || (string.Compare(s.OriginalId, lowerFrom) >= 0 && string.Compare(s.OriginalId, lowerTo) <= 0)
                || (string.Compare(s.OriginalId, upperFrom) >= 0 && string.Compare(s.OriginalId, upperTo) <= 0));
        }

        return rows.Where(s => s.OriginalId == id);
    }

    /// <summary>
    /// Reads every record type through <paramref name="fetch"/>. Types are read sequentially: they
    /// share a scoped DbContext, which is not thread-safe, so they cannot be run concurrently via
    /// <see cref="Task.WhenAll(Task[])"/>. A type whose read fails contributes nothing rather than
    /// failing the whole page.
    /// </summary>
    private async Task<List<FetchedRecord>> FetchAsync(
        Func<ILegacyTreatmentTable, Task<IReadOnlyList<FetchedRecord>>> fetch
    )
    {
        var rows = new List<FetchedRecord>();
        foreach (var table in LegacyTreatmentTables.All)
            rows.AddRange(await FetchSafe(table, fetch));

        return rows;
    }

    /// <summary>
    /// Assembles a page, serving the Note a span's treatment wrote beside it before
    /// <see cref="LegacyTreatmentTables.NotesKey"/> was kept on the span as part of the span.
    /// </summary>
    private async Task<List<Treatment>> AssembleAsync(
        IReadOnlyList<FetchedRecord> rows, CancellationToken ct
    )
    {
        var noteKeys = rows
            .Select(r => r.Record)
            .OfType<Note>()
            .Select(n => n.LegacyId)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (noteKeys.Count > 0)
        {
            var onSpans = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in LegacyTreatmentTables.StateSpanTables)
            {
                onSpans.UnionWith(await FetchSafe(
                    table,
                    _ => table.Rows(_dbContext)
                        .Where(s => noteKeys.Contains(s.OriginalId!))
                        .Select(s => s.OriginalId!)
                        .ToListAsync(ct),
                    new List<string>()));
            }
            if (onSpans.Count > 0)
                rows = rows.Where(r => r.Record is not Note { LegacyId: { } key } || !onSpans.Contains(key)).ToList();
        }

        var spanKeys = rows
            .Select(r => r.Record)
            .OfType<StateSpanEntity>()
            .Select(s => s.OriginalId)
            .OfType<string>()
            .Distinct()
            .ToList();
        Dictionary<string, string>? spanNotes = null;
        if (spanKeys.Count > 0)
        {
            spanNotes = (await _dbContext.Notes.AsNoTracking()
                    .Where(n => n.LegacyId != null && spanKeys.Contains(n.LegacyId))
                    .OrderBy(n => n.Id)
                    .Select(n => new { Key = n.LegacyId!, n.Text })
                    .ToListAsync(ct))
                .GroupBy(n => n.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
        }

        return LegacyTreatmentTables.Assemble(rows, await LoadCarbFoodsAsync(rows, ct), spanNotes);
    }

    private async Task<CarbFoodIndex> LoadCarbFoodsAsync(
        IReadOnlyList<FetchedRecord> rows,
        CancellationToken ct
    )
    {
        var carbIntakeIds = rows
            .Select(r => r.Record)
            .OfType<CarbIntake>()
            .Select(c => c.Id)
            .ToList();

        return carbIntakeIds.Count == 0
            ? CarbFoodIndex.Empty
            : new CarbFoodIndex(await _treatmentFoodService.GetByCarbIntakeIdsAsync(carbIntakeIds, ct));
    }

    private Task<IReadOnlyList<FetchedRecord>> FetchSafe(
        ILegacyTreatmentTable table,
        Func<ILegacyTreatmentTable, Task<IReadOnlyList<FetchedRecord>>> fetch
    ) => FetchSafe(table, fetch, []);

    /// <summary>
    /// A read of one type the database provider cannot translate logs and yields
    /// <paramref name="fallback"/>, so the other types still serve. Any other failure, a cancellation
    /// included, propagates.
    /// </summary>
    private async Task<T> FetchSafe<T>(
        ILegacyTreatmentTable table,
        Func<ILegacyTreatmentTable, Task<T>> fetch,
        T fallback
    )
    {
        try
        {
            return await fetch(table);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            _logger.LogError(
                ex,
                "Failed to fetch V4 records of type {Type} for legacy projection",
                table.RecordType);
            return fallback;
        }
    }

    private static Entry ProjectSensorGlucoseToEntry(SensorGlucose sg) =>
        SensorGlucoseToEntryMapper.ToEntry(sg);
}
