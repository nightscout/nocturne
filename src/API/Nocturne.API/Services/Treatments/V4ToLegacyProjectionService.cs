using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Nocturne.API.Services.Entries;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Projects V4 granular records back into the legacy <see cref="Treatment"/> shape for v1/v2/v3 API
/// compatibility.
/// </summary>
/// <remarks>
/// This service is the read side of the dual-path architecture.
/// The write side (legacy record → V4 typed record) is handled by <see cref="ITreatmentDecomposer"/>.
/// Projection covers: <see cref="Bolus"/> and <see cref="CarbIntake"/> → <see cref="Treatment"/>,
/// <see cref="DeviceEvent"/> → <see cref="Treatment"/> using the legacy event-type map, and the
/// <see cref="StateSpan"/>s and <see cref="ProfileActivation"/>s a legacy treatment was decomposed
/// into back into that treatment.
/// <para>
/// Every treatment read projects records of both provenances, since treatments have no legacy table
/// left to supplement.
/// </para>
/// </remarks>
/// <seealso cref="IV4ToLegacyProjectionService"/>
/// <seealso cref="LegacyTreatmentTables"/>
internal class V4ToLegacyProjectionService : IV4ToLegacyProjectionService
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
        IProfileActivationRepository profileActivationRepository,
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
            bolusCalculationRepository,
            profileActivationRepository
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
        bool descending = true,
        CancellationToken ct = default
    )
    {
        await using var db = await _contexts.CreateAsync(ct);
        var range = new LegacyTreatmentRange(
            MillsToDateTime(fromMills), MillsToDateTime(toMills), limit, descending);

        var rows = await FetchAsync(table => table.InRangeAsync(_repositories, db, range, ct));
        var treatments = await AssembleAsync(db, rows, ct);

        return (descending
                ? treatments.OrderByDescending(t => t.Mills)
                : treatments.OrderBy(t => t.Mills))
            .Take(limit);
    }

    /// <summary>The records of one table <see cref="GetProjectedTreatmentsAtAsync"/> reads at each instant.</summary>
    private const int RowsPerInstant = 100;

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsAtAsync(
        IReadOnlyCollection<long> instants,
        CancellationToken ct = default
    )
    {
        var at = instants.Distinct().Select(mills => MillsToDateTime(mills)!.Value).ToList();
        if (at.Count == 0)
            return [];

        await using var db = await _contexts.CreateAsync(ct);
        var rows = await FetchAsync(table => table.AtAsync(db, at, RowsPerInstant, ct));
        return await AssembleAsync(db, rows, ct, pairedWithin: StartOf);
    }

    private static DateTime StartOf(FetchedRecord row) => row.Record switch
    {
        IV4Record record => record.Timestamp,
        StateSpanEntity span => span.StartTimestamp,
        _ => throw new UnreachableException(),
    };

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
        var lastMills = page[^1].Modified.ToMills();
        var seen = page.Select(RecordId).ToHashSet();

        foreach (var (row, _) in ordered)
        {
            if (row.Modified.ToMills() == lastMills
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

    /// <summary>Span ids <see cref="GetStateSpanTreatmentIdsAsync"/> reads in one statement.</summary>
    internal const int SpanIdsPerStatement = 50;

    /// <inheritdoc />
    /// <remarks>
    /// Every statement matches exactly <see cref="SpanIdsPerStatement"/> ids, the last repeated to fill
    /// it, through <see cref="SpanIdBounds"/>, so every read has one shape whatever ids it is asked for.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetStateSpanTreatmentIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = ids.Distinct(StringComparer.Ordinal).ToList();
        if (unresolved.Count == 0)
            return keys;

        await using var db = await _contexts.CreateAsync(ct);
        foreach (var table in LegacyTreatmentTables.StateSpanTables)
        {
            if (unresolved.Count == 0)
                break;

            var spans = new List<StateSpanEntity>();
            foreach (var chunk in unresolved.Chunk(SpanIdsPerStatement))
            {
                var bounds = chunk.Concat(Enumerable.Repeat(chunk[^1], SpanIdsPerStatement - chunk.Length))
                    .Select(SpanIdBounds.Of);
                spans.AddRange(await table.Rows(db).Where(SpanIdBounds.MatchingAny(bounds)).ToListAsync(ct));
            }

            foreach (var id in unresolved.ToList())
            {
                var bounds = SpanIdBounds.Of(id);
                var span = spans.Where(bounds.Matches)
                    .OrderBy(s => s.OriginalId == id ? 0 : 1)
                    .ThenBy(s => s.Id.ToString("N"), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (span?.OriginalId is { } key)
                {
                    keys[id] = key;
                    unresolved.Remove(id);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// The spans one id names, as <see cref="MatchingId"/> finds them, in one form for every kind of id:
    /// the span keyed to it, the uuids <see cref="Low"/> through <see cref="High"/>, and the original ids
    /// from <see cref="From"/> through <see cref="To"/> in lower case or upper case. A uuid names one
    /// uuid; an id that is neither names only the span keyed to it.
    /// </summary>
    private readonly record struct SpanIdBounds(
        string Id, Guid Low, Guid High, string From, string To, string UpperFrom, string UpperTo)
    {
        public static SpanIdBounds Of(string id)
        {
            if (Guid.TryParse(id, out var guid))
                return new(id, guid, guid, guid.ToString(), guid.ToString(),
                    guid.ToString().ToUpperInvariant(), guid.ToString().ToUpperInvariant());

            return MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high)
                ? new(id, low, high, low.ToString(), high.ToString(),
                    low.ToString().ToUpperInvariant(), high.ToString().ToUpperInvariant())
                : new(id, Guid.Empty, Guid.Empty, id, id, id, id);
        }

        public bool Matches(StateSpanEntity span)
        {
            static bool Between(string? value, string from, string to) =>
                value is not null && string.CompareOrdinal(value, from) >= 0 && string.CompareOrdinal(value, to) <= 0;
            return span.OriginalId == Id
                || Between(span.Id.ToString(), Low.ToString(), High.ToString())
                || Between(span.OriginalId, From, To)
                || Between(span.OriginalId, UpperFrom, UpperTo);
        }

        public static Expression<Func<StateSpanEntity, bool>> MatchingAny(IEnumerable<SpanIdBounds> bounds)
        {
            Expression<Func<StateSpanEntity, bool>> none = s => false;
            var row = none.Parameters[0];
            Expression? matches = null;
            foreach (var (id, low, high, from, to, upperFrom, upperTo) in bounds)
            {
                Expression<Func<StateSpanEntity, bool>> term = s => s.OriginalId == id
                    || (s.Id >= low && s.Id <= high)
                    || (string.Compare(s.OriginalId, from) >= 0 && string.Compare(s.OriginalId, to) <= 0)
                    || (string.Compare(s.OriginalId, upperFrom) >= 0 && string.Compare(s.OriginalId, upperTo) <= 0);
                var body = ReplacingExpressionVisitor.Replace(term.Parameters[0], row, term.Body);
                matches = matches is null ? body : Expression.OrElse(matches, body);
            }

            return Expression.Lambda<Func<StateSpanEntity, bool>>(matches ?? none.Body, row);
        }
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
    public async Task<long> CountProjectedTreatmentsAsync(
        long? fromMills, long? toMills, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var (from, to) = (MillsToDateTime(fromMills), MillsToDateTime(toMills));
        long count = 0;
        foreach (var table in LegacyTreatmentTables.All)
            count += await table.CountAsync(_repositories, db, from, to, _logger, ct);

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
    /// <param name="pairedWithin">
    /// When set, a meal pairs only rows that share this key, as separate reads of each key would.
    /// </param>
    private async Task<List<Treatment>> AssembleAsync(
        NocturneDbContext db,
        IReadOnlyList<FetchedRecord> rows, CancellationToken ct,
        DateTime? historyCursor = null,
        Func<FetchedRecord, DateTime>? pairedWithin = null
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

        var foods = await LoadCarbFoodsAsync(rows, ct);
        var treatments = pairedWithin is null
            ? LegacyTreatmentTables.Assemble(rows, foods, notes)
            : [.. rows.GroupBy(pairedWithin).SelectMany(group => LegacyTreatmentTables.Assemble([.. group], foods, notes))];
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

    /// <inheritdoc cref="LegacyTreatmentTables.GuardedAsync"/>
    private Task<IReadOnlyList<FetchedRecord>> FetchSafe(
        ILegacyTreatmentTable table,
        Func<ILegacyTreatmentTable, Task<IReadOnlyList<FetchedRecord>>> fetch
    ) => LegacyTreatmentTables.GuardedAsync(table, _logger, () => fetch(table), []);
}
