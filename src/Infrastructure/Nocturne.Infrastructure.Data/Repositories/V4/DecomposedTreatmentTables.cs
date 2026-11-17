using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;

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
        new DecomposedTreatmentTable<BolusEntity>(typeof(Bolus), db => db.Boluses, InWindow),
        new DecomposedTreatmentTable<TempBasalEntity>(typeof(TempBasal), db => db.TempBasals, InWindow),
        new DecomposedTreatmentTable<CarbIntakeEntity>(typeof(CarbIntake), db => db.CarbIntakes, InWindow),
        new DecomposedTreatmentTable<BGCheckEntity>(typeof(BGCheck), db => db.BGChecks, InWindow),
        new DecomposedTreatmentTable<NoteEntity>(typeof(Note), db => db.Notes, InWindow),
        new DecomposedTreatmentTable<DeviceEventEntity>(typeof(DeviceEvent), db => db.DeviceEvents, InWindow),
        new DecomposedTreatmentTable<BolusCalculationEntity>(typeof(BolusCalculation), db => db.BolusCalculations, InWindow),
        new DecomposedTreatmentTable<ProfileActivationEntity>(typeof(ProfileActivation), db => db.ProfileActivations, ServedInWindow),
    ];

    /// <summary>The rows of <paramref name="rows"/> whose event time is inside the window, bounds inclusive.</summary>
    public static IQueryable<T> InWindow<T>(IQueryable<T> rows, DateTime? from, DateTime? to)
        where T : IV4TimeSeriesEntity
    {
        if (from.HasValue)
            rows = rows.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            rows = rows.Where(e => e.Timestamp <= to.Value);
        return rows;
    }

    /// <summary>
    /// The activations a window delete takes: those decomposed from a treatment, which every window
    /// read serves. A connector's are not treatments.
    /// </summary>
    private static IQueryable<ProfileActivationEntity> ServedInWindow(
        IQueryable<ProfileActivationEntity> rows, DateTime? from, DateTime? to) =>
        InWindow(rows, from, to).Where(a => a.LegacyId != null);
}

/// <summary>A legacy id one of <see cref="DecomposedTreatmentTables"/> stores, and its row's event time.</summary>
public sealed record StoredLegacyId(string LegacyId, DateTime At);

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

    internal abstract Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
        NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct);

    /// <summary>Stamps this source's rows that carry no fingerprint yet.</summary>
    internal abstract Task StampUnfingerprintedAsync(
        NocturneDbContext db, string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct);

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
    Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inWindow) : DecomposedTreatmentTable(model)
    where T : class, IV4TimeSeriesEntity, IAuditable, IUpstreamFingerprinted
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

    internal override async Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
        NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct)
        => (await FromSource(rowsOf(db).AsNoTracking(), source, legacyIds)
                .Select(e => new { LegacyId = e.LegacyId!, e.UpstreamFingerprint })
                .ToListAsync(ct))
            .Select(e => (e.LegacyId, e.UpstreamFingerprint))
            .ToList();

    internal override async Task StampUnfingerprintedAsync(
        NocturneDbContext db, string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct)
    {
        foreach (var (legacyId, fingerprint) in fingerprints)
        {
            await FromSource(rowsOf(db), source, [legacyId])
                .Where(e => e.UpstreamFingerprint == null)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.UpstreamFingerprint, fingerprint), ct);
        }
    }

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
