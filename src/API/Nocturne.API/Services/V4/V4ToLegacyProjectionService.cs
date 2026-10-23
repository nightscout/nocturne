using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Projects V4 granular records back into the legacy <see cref="Treatment"/> shape for v1/v2/v3 API
/// compatibility.
/// </summary>
/// <remarks>
/// This service is the read side of the dual-path architecture.
/// The write side (legacy record → V4 typed record) is handled by <see cref="DecompositionPipeline"/>.
/// Projection covers: <see cref="Bolus"/> and <see cref="CarbIntake"/> → <see cref="Treatment"/>,
/// <see cref="DeviceEvent"/> → <see cref="Treatment"/> using the legacy event-type map, and the
/// <see cref="StateSpan"/>s and <see cref="ProfileActivation"/>s a legacy treatment was decomposed
/// into back into that treatment.
/// <para>
/// Every treatment read projects records of both provenances, since treatments have no legacy table
/// left to supplement. See <see cref="LegacyTreatmentRange.NativeOnly"/>.
/// </para>
/// </remarks>
/// <seealso cref="IV4ToLegacyProjectionService"/>
/// <seealso cref="LegacyTreatmentTables"/>
/// <seealso cref="DecompositionPipeline"/>
public class V4ToLegacyProjectionService : IV4ToLegacyProjectionService
{
    private readonly LegacyTreatmentRepositories _repositories;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<V4ToLegacyProjectionService> _logger;

    public V4ToLegacyProjectionService(
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        ITempBasalRepository tempBasalRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITreatmentFoodService treatmentFoodService,
        INocturneDbContextFactory contexts,
        ILogger<V4ToLegacyProjectionService> logger
    )
    {
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
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _logger = logger;
    }

    /// <summary>
    /// Converts nullable unix milliseconds to nullable DateTime.
    /// </summary>
    private static DateTime? MillsToDateTime(long? mills) =>
        mills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value).UtcDateTime : null;

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        int limit,
        bool nativeOnly = true,
        bool descending = true,
        CancellationToken ct = default
    )
    {
        await using var db = await _contexts.CreateAsync(ct);
        var range = new LegacyTreatmentRange(
            MillsToDateTime(fromMills), MillsToDateTime(toMills), limit, nativeOnly, descending);

        var rows = await FetchAsync(table => table.InRangeAsync(_repositories, db, range, ct));
        var treatments = await AssembleAsync(db, rows, ct);

        return (descending
                ? treatments.OrderByDescending(t => t.Mills)
                : treatments.OrderBy(t => t.Mills))
            .Take(limit);
    }

    /// <inheritdoc />
    public async Task<Treatment?> GetLatestProjectedTreatmentAsync(
        string eventType, long fromMills, long toMills, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var rows = await LegacyTreatmentTables.LatestOfTypeAsync(
            _repositories, db, eventType, MillsToDateTime(fromMills)!.Value, MillsToDateTime(toMills)!.Value, ct);

        return (await AssembleAsync(db, rows, ct)).FirstOrDefault();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each table pages independently on a millisecond boundary; see <see cref="HistoryPage"/>.
    /// </remarks>
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsModifiedSinceAsync(
        long lastModifiedMills, int limit, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        using var horizon = HistoryHorizon.Share();
        var perTable = new List<(ILegacyTreatmentTable Table, int Order, IReadOnlyList<FetchedRecord> Rows)>();
        foreach (var table in LegacyTreatmentTables.All)
        {
            var rows = await FetchSafe(
                table, t => t.ModifiedSinceAsync(db, lastModifiedMills, limit, _logger, ct));
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

        var newest = page.Count == 0 ? (DateTime?)null : page.Max(r => r.Modified);
        page = await WithLiveMealPartnersAsync(db, page, ct);

        var treatments = await AssembleAsync(db, page, ct, newest);

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
        NocturneDbContext db,
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
            var live = await FetchSafe(table, t => t.LiveByCorrelationAsync(db, deletes.Keys, ct));
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
    public async Task<Treatment?> GetProjectedStateSpanTreatmentAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await FindStateSpanAsync(db, id, ct) is { } row
            ? (await AssembleAsync(db, [row], ct)).Single()
            : null;
    }

    /// <inheritdoc />
    /// <remarks>Resolves an id as <see cref="GetProjectedStateSpanTreatmentAsync"/> does.</remarks>
    public async Task<string?> GetStateSpanTreatmentIdAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await FindStateSpanAsync(db, id, ct) is { } row ? ((StateSpanEntity)row.Record).OriginalId : null;
    }

    private async Task<FetchedRecord?> FindStateSpanAsync(NocturneDbContext db, string id, CancellationToken ct)
    {
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            var span = await MatchingId(table.Rows(db), id)
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
        await using var db = await _contexts.CreateAsync(ct);
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            if (await MatchingId(table.UserTombstones(db), id).AnyAsync(ct))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<long> CountProjectedStateSpanTreatmentsAsync(
        long? fromMills, long? toMills, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var (from, to) = (MillsToDateTime(fromMills), MillsToDateTime(toMills));
        long count = 0;
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            count += await FetchSafe(
                table, _ => table.InWindow(db, from, to).LongCountAsync(ct), 0L);
        }

        var activations = LegacyTreatmentTables.ProfileActivationTable;
        count += await FetchSafe(
            activations, _ => LegacyProfileActivationTable.InWindow(db, from, to).LongCountAsync(ct), 0L);

        return count;
    }

    /// <inheritdoc />
    public async Task<long> CountNotesServedApartAsync(
        long? fromMills, long? toMills, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        IQueryable<NoteEntity> InWindow(IQueryable<NoteEntity> notes)
        {
            notes = notes.AsNoTracking().ExcludeNonPrimary(db, RecordType.Note);
            if (MillsToDateTime(fromMills) is { } lower)
                notes = notes.Where(n => n.Timestamp >= lower);
            if (MillsToDateTime(toMills) is { } upper)
                notes = notes.Where(n => n.Timestamp <= upper);
            return notes;
        }

        return await InWindow(db.Notes).LongCountAsync(ct)
            - await InWindow(LegacyTreatmentTables.NotesServedWithAnotherRecord(db)).LongCountAsync(ct);
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
    /// share the caller's context, which is not thread-safe, so they cannot be run concurrently via
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
    /// Assembles a page, serving the Note a record's treatment wrote beside it as part of that record
    /// (<see cref="LegacyTreatmentTables.NotesServedWithAnotherRecord"/>), stamped no older than the
    /// Note's row, deleted or not, so a conditional read sees an edit of the notes alone.
    /// </summary>
    /// <param name="historyCursor">
    /// The newest row stamp of a history page, or null for any other read. A history page also serves:
    /// the records holding a Note on it that the page does not, since editing only the notes moves the
    /// Note's row alone; a held Note as a tombstone under its own id, which a client that synced it as
    /// a treatment of its own before it was served as part of its record still holds; and the live
    /// Note of a record deleted on the page that leaves no other, which a search now serves apart. No
    /// row is stamped above <paramref name="historyCursor"/>, so the client's cursor stays where the
    /// page put it.
    /// </param>
    private async Task<List<Treatment>> AssembleAsync(
        NocturneDbContext db,
        IReadOnlyList<FetchedRecord> rows, CancellationToken ct,
        DateTime? historyCursor = null
    )
    {
        var foldedNotes = new HashSet<string>(StringComparer.Ordinal);
        var noteKeys = rows
            .Select(r => r.Record)
            .OfType<Note>()
            .Select(n => n.LegacyId)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (noteKeys.Count > 0)
        {
            var onPage = rows
                .Select(r => LegacyTreatmentTables.ServedKey(r.Record))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var holders = new List<FetchedRecord>();
            foreach (var table in LegacyTreatmentTables.All.Where(t => t.RecordType != nameof(Note)))
                holders.AddRange(await FetchSafe(table, t => t.LiveByTreatmentKeyAsync(db, noteKeys, ct)));
            var live = holders
                .Select(r => LegacyTreatmentTables.ServedKey(r.Record))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            // A span or activation served its treatment's Note before records did, so no client
            // holds that Note apart.
            var heldByRecords = holders
                .Concat(rows.Where(r => r.Deleted))
                .Where(r => r.Record is not (Note or StateSpanEntity or ProfileActivation))
                .Select(r => LegacyTreatmentTables.ServedKey(r.Record))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            bool Held(FetchedRecord row) =>
                row.Record is Note { LegacyId: { } key } && (live.Contains(key) || (row.Deleted && onPage.Contains(key)));

            if (historyCursor is null)
            {
                rows = rows.Where(r => !Held(r)).ToList();
            }
            else
            {
                var seen = rows.Select(RecordId).ToHashSet();
                var kept = rows
                    .Where(r => !Held(r) || heldByRecords.Contains(((Note)r.Record).LegacyId!))
                    .ToList();
                foldedNotes.UnionWith(kept.Where(Held).Select(r => RecordId(r).ToString()));
                rows =
                [
                    .. kept.Select(r => Held(r) ? r with { Deleted = true } : r),
                    .. holders.Where(h => seen.Add(RecordId(h))),
                ];
            }
        }

        if (historyCursor is not null)
            rows = await WithNotesOfDeletedRecordsAsync(db, rows, ct);

        var keys = rows
            .Select(r => LegacyTreatmentTables.ServedKey(r.Record))
            .OfType<string>()
            .Distinct()
            .ToList();
        Dictionary<string, string>? notes = null;
        Dictionary<string, DateTime> noteStamps = [];
        if (keys.Count > 0)
        {
            var written = await db.Notes.IncludingDeleted().AsNoTracking()
                .Where(n => n.LegacyId != null && keys.Contains(n.LegacyId))
                .OrderBy(n => n.Id)
                .Select(n => new { Key = n.LegacyId!, n.Text, Live = n.DeletedAt == null, n.SysUpdatedAt })
                .ToListAsync(ct);
            notes = written
                .Where(n => n.Live)
                .GroupBy(n => n.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
            noteStamps = written
                .GroupBy(n => n.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(n => n.SysUpdatedAt), StringComparer.Ordinal);
        }

        rows = rows.Select(r => r with { Modified = StampOf(r) }).ToList();

        DateTime StampOf(FetchedRecord row)
        {
            var stamp = row.Modified;
            if (row.Record is not Note
                && LegacyTreatmentTables.ServedKey(row.Record) is { } key
                && noteStamps.TryGetValue(key, out var noteStamp)
                && noteStamp > stamp)
                stamp = noteStamp;
            return historyCursor is { } cursor && stamp > cursor ? cursor : stamp;
        }

        var treatments = LegacyTreatmentTables.Assemble(rows, await LoadCarbFoodsAsync(rows, ct), notes);
        return foldedNotes.Count == 0
            ? treatments
            : treatments.Select(t => t.Id is { } id && foldedNotes.Contains(id) ? FoldedNoteTombstone(t) : t).ToList();
    }

    /// <summary>
    /// A folded Note's tombstone carries its identity and deletion alone: its event type and sync
    /// identifier are its treatment's, and a client matching on those would take it for the record
    /// that is still served.
    /// </summary>
    private static Treatment FoldedNoteTombstone(Treatment note) => new()
    {
        Id = note.Id,
        EventType = "Note",
        Mills = note.Mills,
        SrvCreated = note.SrvCreated,
        SrvModified = note.SrvModified,
        IsValid = false,
    };

    /// <summary>
    /// Adds the live Notes of the records deleted on a history page whose treatment keeps no other
    /// live record, stamped with that delete: a search serves such a Note as a treatment of its own,
    /// and its row did not move.
    /// </summary>
    private async Task<IReadOnlyList<FetchedRecord>> WithNotesOfDeletedRecordsAsync(
        NocturneDbContext db, IReadOnlyList<FetchedRecord> rows, CancellationToken ct)
    {
        var deletes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Deleted && r.Record is not Note))
        {
            if (LegacyTreatmentTables.ServedKey(row.Record) is not { } key)
                continue;
            if (!deletes.TryGetValue(key, out var stamp) || row.Modified > stamp)
                deletes[key] = row.Modified;
        }

        foreach (var row in rows.Where(r => !r.Deleted && r.Record is not Note))
        {
            if (LegacyTreatmentTables.ServedKey(row.Record) is { } key)
                deletes.Remove(key);
        }

        if (deletes.Count == 0)
            return rows;

        var keys = deletes.Keys.ToList();
        foreach (var table in LegacyTreatmentTables.All.Where(t => t.RecordType != nameof(Note)))
        {
            foreach (var holder in await FetchSafe(table, t => t.LiveByTreatmentKeyAsync(db, keys, ct)))
            {
                if (LegacyTreatmentTables.ServedKey(holder.Record) is { } key)
                    deletes.Remove(key);
            }
        }

        if (deletes.Count == 0)
            return rows;

        var noteTable = LegacyTreatmentTables.All.Single(t => t.RecordType == nameof(Note));
        var seen = rows.Select(RecordId).ToHashSet();
        var notes = (await FetchSafe(noteTable, t => t.LiveByTreatmentKeyAsync(db, deletes.Keys, ct)))
            .Where(n => seen.Add(RecordId(n)))
            .Select(n => n with { Modified = deletes[((Note)n.Record).LegacyId!] });
        return [.. rows, .. notes];
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
}
