using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The tables a legacy treatment decomposes into, each row keyed by the treatment's id as its
/// <see cref="ILegacyIdentified.LegacyId"/>. Deletes go through the audited soft-delete path: a
/// user-issued one is attributed, and blocks a later connector resync from re-creating the row
/// (<see cref="SoftDeleteDedupExtensions"/>).
/// </summary>
/// <remarks>
/// Public only because the treatment delete paths still build their queries in the host, over
/// <c>LegacyTreatmentTables</c>. Only the treatment decomposer may use it
/// (<c>DecomposedTreatmentTablesGuardTests</c>).
/// </remarks>
public static class DecomposedTreatmentTables
{
    public static IReadOnlyList<DecomposedTreatmentTable> All { get; } =
    [
        Table<Bolus, BolusEntity>(db => db.Boluses, ObservationWindow.InWindow, BolusMapper.ToEntity),
        Table<TempBasal, TempBasalEntity>(db => db.TempBasals, ObservationWindow.InWindow, TempBasalMapper.ToEntity),
        Table<CarbIntake, CarbIntakeEntity>(db => db.CarbIntakes, ObservationWindow.InWindow, CarbIntakeMapper.ToEntity),
        Table<BGCheck, BGCheckEntity>(db => db.BGChecks, ObservationWindow.InWindow, BGCheckMapper.ToEntity),
        Table<Note, NoteEntity>(db => db.Notes, ObservationWindow.InWindow, NoteMapper.ToEntity),
        Table<DeviceEvent, DeviceEventEntity>(db => db.DeviceEvents, ObservationWindow.InWindow, DeviceEventMapper.ToEntity),
        Table<BolusCalculation, BolusCalculationEntity>(db => db.BolusCalculations, ObservationWindow.InWindow, BolusCalculationMapper.ToEntity),
        Table<ProfileActivation, ProfileActivationEntity>(db => db.ProfileActivations, ServedInWindow, ProfileActivationMapper.ToEntity),
    ];

    private static DecomposedTreatmentTable<TEntity> Table<TModel, TEntity>(
        Func<NocturneDbContext, DbSet<TEntity>> rowsOf,
        Func<IQueryable<TEntity>, DateTime?, DateTime?, IQueryable<TEntity>> inWindow,
        Func<TModel, TEntity> toEntity)
        where TModel : IV4Record
        where TEntity : class, IV4TimeSeriesEntity, IAuditable, IUpstreamFingerprinted, IWriteBackSent
        => new(typeof(TModel), rowsOf, inWindow, record => toEntity((TModel)record));

    /// <summary>The table whose rows <paramref name="record"/> is stored as.</summary>
    internal static DecomposedTreatmentTable Of(IV4Record record) =>
        All.FirstOrDefault(t => t.Model.IsInstanceOfType(record))
        ?? throw new ArgumentException($"{record.GetType().Name} is not a decomposed treatment record", nameof(record));

    /// <summary>
    /// The activations a window delete takes: those decomposed from a treatment, which every window
    /// read serves. A connector's are not treatments.
    /// </summary>
    private static IQueryable<ProfileActivationEntity> ServedInWindow(
        IQueryable<ProfileActivationEntity> rows, DateTime? from, DateTime? to) =>
        rows.InWindow(from, to).Where(a => a.LegacyId != null);
}

/// <summary>A legacy id one of <see cref="DecomposedTreatmentTables"/> stores, and its row's event time.</summary>
public sealed record StoredLegacyId(string LegacyId, DateTime At);

/// <summary>A live row of a record write-back sends, the documents of that send, and what the row stores of earlier ones.</summary>
internal sealed record SentRow(Guid Id, IReadOnlyList<string> Documents, string? Stored);

/// <summary>One of <see cref="DecomposedTreatmentTables"/>.</summary>
public abstract class DecomposedTreatmentTable(Type model)
{
    /// <summary>The model type the table's rows are served as, which a write to it reports.</summary>
    public Type Model { get; } = model;

    /// <param name="db">The context whose rows are deleted.</param>
    /// <param name="legacyIds">The legacy ids whose rows are deleted.</param>
    /// <param name="source">Only this data source's rows, or every source's when null.</param>
    /// <param name="audit">The audit context the delete is attributed through.</param>
    /// <param name="scope">The scope the delete is recorded under.</param>
    /// <param name="duplicates">How duplicate copies of the deleted rows are handled.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <remarks>
    /// A legacy id fans out to a handful of correlated rows, so even a batch's ids reach few. The
    /// per-record audit rows <see cref="AuditedBulkDeleteExtensions.AuditedSoftDeleteWithEntitiesAsync{T}"/>
    /// writes below its cap are the right shape for that.
    /// </remarks>
    public abstract Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
        NocturneDbContext db, string[] legacyIds, string? source, IAuditContext audit, string scope,
        DuplicateDelete duplicates, CancellationToken ct);

    public abstract Task<int> SoftDeleteInWindowAsync(
        NocturneDbContext db, DateTime? from, DateTime? to, IAuditContext audit, string scope, CancellationToken ct);

    /// <summary>The ids of the rows a window delete of this table takes.</summary>
    public abstract Task<List<Guid>> IdsInWindowAsync(NocturneDbContext db, DateTime? from, DateTime? to, CancellationToken ct);

    public abstract Task<List<StoredLegacyId>> StoredFromSourceAsync(
        NocturneDbContext db, string source, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>The ids of <paramref name="legacyIds"/> that block re-creation, and whether a live row holds each.</summary>
    internal abstract Task<IEnumerable<(string Key, bool Live)>> BlockingAsync(
        NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct);

    /// <summary>Stages <paramref name="record"/> as a row already soft-deleted at <paramref name="deletedAt"/>.</summary>
    internal abstract void AddDeleted(NocturneDbContext db, IV4Record record, DateTime deletedAt);

    /// <summary>The ids of <paramref name="legacyIds"/> a soft-deleted row is stored under, whoever deleted it.</summary>
    internal abstract Task<List<string>> DeletedAsync(
        NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct);

    internal abstract Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
        NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct);

    /// <summary>
    /// Stamps this source's rows under <paramref name="legacyIds"/> that carry no fingerprint yet, each
    /// with the fingerprint at its id's index in <paramref name="fingerprints"/>, in one statement.
    /// </summary>
    internal abstract Task StampUnfingerprintedAsync(
        NocturneDbContext db, string source, string[] legacyIds, string[] fingerprints, CancellationToken ct);

    /// <summary>The correlation id of each row of this table among <paramref name="ids"/> that has one.</summary>
    internal abstract Task<List<(Guid Id, Guid CorrelationId)>> CorrelationsAsync(NocturneDbContext db, Guid[] ids, CancellationToken ct);

    /// <summary>
    /// The live rows of a record of <paramref name="byRow"/>, <paramref name="byLegacyId"/> or, unkeyed,
    /// <paramref name="byCorrelation"/>, matched in that order, each with its record's documents and
    /// what it stores (<see cref="WriteBackFingerprint"/>).
    /// </summary>
    internal abstract Task<List<SentRow>> SentRowsAsync(
        NocturneDbContext db,
        IReadOnlyDictionary<string, IReadOnlyList<string>> byLegacyId,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> byRow,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> byCorrelation,
        CancellationToken ct);

    /// <summary>Sets what each row of <paramref name="stored"/> stores of write-back, in one statement.</summary>
    internal abstract Task StampSentAsync(NocturneDbContext db, IReadOnlyDictionary<Guid, string> stored, CancellationToken ct);

    /// <summary>The ids of <paramref name="legacyIds"/> a row of this table holds a sent fingerprint under.</summary>
    internal abstract Task<List<string>> SentUpstreamAsync(NocturneDbContext db, string[] legacyIds, CancellationToken ct);

    /// <summary>This source's live rows and user tombstones under <paramref name="legacyIds"/>.</summary>
    internal abstract Task<List<(StoredLegacyId Row, bool Live)>> ClientIdRowsAsync(
        NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct);

    /// <summary>Moves this source's live rows and user tombstones from one legacy id to another.</summary>
    internal abstract Task<int> RekeyAsync(NocturneDbContext db, string source, string from, string to, CancellationToken ct);

    /// <summary>Stages a copy of this source's user tombstones under <paramref name="legacyId"/> for each of <paramref name="copyTo"/>.</summary>
    internal abstract Task AddTombstoneCopiesAsync(
        NocturneDbContext db, string source, string legacyId, string[] copyTo, CancellationToken ct);
}

/// <inheritdoc cref="DecomposedTreatmentTable"/>
internal sealed class DecomposedTreatmentTable<T>(
    Type model,
    Func<NocturneDbContext, DbSet<T>> rowsOf,
    Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inWindow,
    Func<IV4Record, T> toEntity) : DecomposedTreatmentTable(model)
    where T : class, IV4TimeSeriesEntity, IAuditable, IUpstreamFingerprinted, IWriteBackSent
{
    public override Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
        NocturneDbContext db, string[] legacyIds, string? source, IAuditContext audit, string scope,
        DuplicateDelete duplicates, CancellationToken ct)
        => db.AuditedSoftDeleteWithIdsAsync(
            rowsOf(db).Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId)
                         && (source == null || e.DataSource == source)),
            audit, scope, ct, duplicates);

    public override Task<int> SoftDeleteInWindowAsync(
        NocturneDbContext db, DateTime? from, DateTime? to, IAuditContext audit, string scope, CancellationToken ct)
        => db.AuditedSoftDeleteAsync(inWindow(rowsOf(db), from, to), audit, scope, ct);

    public override Task<List<Guid>> IdsInWindowAsync(NocturneDbContext db, DateTime? from, DateTime? to, CancellationToken ct)
        => inWindow(rowsOf(db), from, to).AsNoTracking().Select(e => e.Id).ToListAsync(ct);

    public override Task<List<StoredLegacyId>> StoredFromSourceAsync(
        NocturneDbContext db, string source, DateTime from, DateTime to, CancellationToken ct)
        => StoredAt(inWindow(rowsOf(db).AsNoTracking(), from, to)
                .Where(e => e.DataSource == source && e.LegacyId != null))
            .ToListAsync(ct);

    internal override async Task<IEnumerable<(string Key, bool Live)>> BlockingAsync(
        NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct)
    {
        var blocks = await db.GetBlockingLegacyIdsAsync<T>(legacyIds, ct);
        return blocks.Held.Select(id => (id, !blocks.DeletedByUser.Contains(id)));
    }

    internal override void AddDeleted(NocturneDbContext db, IV4Record record, DateTime deletedAt)
    {
        var row = toEntity(record);
        row.DeletedAt = deletedAt;
        rowsOf(db).Add(row);
    }

    internal override Task<List<string>> DeletedAsync(
        NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct)
        => rowsOf(db).IncludingDeleted().AsNoTracking()
            .Where(e => e.DeletedAt != null && e.LegacyId != null && legacyIds.Contains(e.LegacyId))
            .Select(e => e.LegacyId!)
            .Distinct()
            .ToListAsync(ct);

    internal override async Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
        NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct)
        => (await FromSource(rowsOf(db).AsNoTracking(), source, legacyIds)
                .Select(e => new { LegacyId = e.LegacyId!, e.UpstreamFingerprint })
                .ToListAsync(ct))
            .Select(e => (e.LegacyId, e.UpstreamFingerprint))
            .ToList();

    internal override Task StampUnfingerprintedAsync(
        NocturneDbContext db, string source, string[] legacyIds, string[] fingerprints, CancellationToken ct)
        => FromSource(rowsOf(db), source, legacyIds)
            .Where(e => e.UpstreamFingerprint == null)
            .ExecuteUpdateAsync(u => u.SetProperty(
                e => e.UpstreamFingerprint, e => fingerprints[Array.IndexOf(legacyIds, e.LegacyId!)]), ct);

    internal override async Task<List<(Guid Id, Guid CorrelationId)>> CorrelationsAsync(
        NocturneDbContext db, Guid[] ids, CancellationToken ct)
        => (await rowsOf(db).AsNoTracking()
                .Where(e => ids.Contains(e.Id) && e.CorrelationId != null && e.CorrelationId != Guid.Empty)
                .Select(e => new { e.Id, CorrelationId = e.CorrelationId!.Value })
                .ToListAsync(ct))
            .Select(e => (e.Id, e.CorrelationId))
            .ToList();

    internal override async Task<List<SentRow>> SentRowsAsync(
        NocturneDbContext db,
        IReadOnlyDictionary<string, IReadOnlyList<string>> byLegacyId,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> byRow,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> byCorrelation,
        CancellationToken ct)
    {
        string[] ids = [.. byLegacyId.Keys];
        Guid[] rowIds = [.. byRow.Keys];
        Guid[] groups = [.. byCorrelation.Keys];
        var rows = await rowsOf(db).AsNoTracking()
            .Where(e => (e.LegacyId != null && ids.Contains(e.LegacyId))
                || rowIds.Contains(e.Id)
                || (e.LegacyId == null && e.CorrelationId != null && groups.Contains(e.CorrelationId.Value)))
            .Select(e => new { e.Id, e.LegacyId, e.CorrelationId, e.WriteBackSentFingerprint })
            .ToListAsync(ct);

        var sent = new List<SentRow>(rows.Count);
        foreach (var row in rows)
        {
            if (byRow.TryGetValue(row.Id, out var documents)
                || (row.LegacyId is { } legacyId && byLegacyId.TryGetValue(legacyId, out documents))
                || (row.LegacyId is null && row.CorrelationId is { } group && byCorrelation.TryGetValue(group, out documents)))
                sent.Add(new SentRow(row.Id, documents, row.WriteBackSentFingerprint));
        }

        return sent;
    }

    internal override async Task StampSentAsync(NocturneDbContext db, IReadOnlyDictionary<Guid, string> stored, CancellationToken ct)
    {
        if (stored.Count == 0)
            return;

        Guid[] rowIds = [.. stored.Keys];
        string[] values = [.. stored.Values];
        await rowsOf(db)
            .Where(e => rowIds.Contains(e.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(
                e => e.WriteBackSentFingerprint, e => values[Array.IndexOf(rowIds, e.Id)]), ct);
    }

    internal override Task<List<string>> SentUpstreamAsync(NocturneDbContext db, string[] legacyIds, CancellationToken ct)
        => rowsOf(db).IncludingDeleted().AsNoTracking()
            .Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId) && e.WriteBackSentFingerprint != null)
            .Select(e => e.LegacyId!)
            .Distinct()
            .ToListAsync(ct);

    internal override async Task<List<(StoredLegacyId Row, bool Live)>> ClientIdRowsAsync(
        NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct)
    {
        var stored = FromSource(BlockingRows(db).AsNoTracking(), source, legacyIds);
        var live = await StoredAt(stored.Where(e => e.DeletedAt == null)).ToListAsync(ct);
        var deleted = await StoredAt(stored.Where(e => e.DeletedAt != null)).ToListAsync(ct);
        return
        [
            .. live.Select(r => (r, Live: true)),
            .. deleted.Select(r => (r, Live: false)),
        ];
    }

    internal override Task<int> RekeyAsync(NocturneDbContext db, string source, string from, string to, CancellationToken ct)
        => FromSource(BlockingRows(db), source, [from]).ExecuteUpdateAsync(u => u.SetProperty(e => e.LegacyId, to), ct);

    internal override async Task AddTombstoneCopiesAsync(
        NocturneDbContext db, string source, string legacyId, string[] copyTo, CancellationToken ct)
    {
        if (copyTo.Length == 0)
            return;

        var tombstones = await FromSource(BlockingRows(db).AsNoTracking(), source, [legacyId])
            .Where(e => e.DeletedAt != null).ToListAsync(ct);
        DecomposedTreatmentKeys.AddTombstoneCopies(db, tombstones, copyTo, (e, id) => e.LegacyId = id);
    }

    private IQueryable<T> BlockingRows(NocturneDbContext db) =>
        rowsOf(db).IncludingDeleted().WhereBlocksRecreation();

    private static IQueryable<StoredLegacyId> StoredAt(IQueryable<T> rows)
        => rows.Select(e => new StoredLegacyId(e.LegacyId!, e.Timestamp));

    private static IQueryable<T> FromSource(IQueryable<T> query, string source, string[] legacyIds)
        => query.Where(e => e.DataSource == source && e.LegacyId != null && legacyIds.Contains(e.LegacyId));
}
