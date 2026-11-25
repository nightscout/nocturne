using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// <see cref="IDecomposedTreatmentKeys"/> over <see cref="DecomposedTreatmentTables"/> and the state
/// spans of overrides and temporary targets, one context of the scope's reader per call.
/// </summary>
public sealed class DecomposedTreatmentKeys(INocturneDbContextFactory contexts, TimeProvider time) : IDecomposedTreatmentKeys
{
    /// <summary>
    /// The categories of the spans a treatment writes in place of a table row. A profile switch is a
    /// profile activation.
    /// </summary>
    public static IReadOnlyList<string> TreatmentSpanCategories { get; } =
    [
        nameof(StateSpanCategory.Override),
        nameof(StateSpanCategory.TemporaryTarget),
    ];

    /// <inheritdoc />
    public async Task<HeldLegacyIds> GetHeldLegacyIdsAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await HeldAsync(db, [.. legacyIds], ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetDeletedLegacyIdsAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        HashSet<string> ids = [.. legacyIds];
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in DecomposedTreatmentTables.All)
            deleted.UnionWith(await table.DeletedAsync(db, ids, ct));

        deleted.UnionWith(await db.StateSpans.IncludingDeleted().AsNoTracking()
            .WhereCategoryIsCurrent()
            .Where(s => s.DeletedAt != null && s.LegacyId != null && ids.Contains(s.LegacyId))
            .Select(s => s.LegacyId!)
            .ToListAsync(ct));
        return deleted;
    }

    /// <inheritdoc />
    /// <remarks>Matches the jsonb boolean, so a string <c>"false"</c> is not taken for it.</remarks>
    public async Task<IReadOnlyList<string>> GetLiveInvalidLegacyIdsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.Database.SqlQuery<string>($"""
            SELECT legacy_id AS "Value" FROM temp_basals
            WHERE deleted_at IS NULL AND legacy_id IS NOT NULL AND (additional_properties -> 'isValid') = 'false'::jsonb
            UNION
            SELECT legacy_id FROM profile_activations
            WHERE deleted_at IS NULL AND legacy_id IS NOT NULL AND (additional_properties -> 'isValid') = 'false'::jsonb
            UNION
            SELECT original_id FROM state_spans
            WHERE deleted_at IS NULL AND original_id IS NOT NULL
              AND category = ANY({TreatmentSpanCategories.ToArray()})
              AND (metadata -> 'isValid') = 'false'::jsonb
            """).OrderBy(id => id).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task StoreDeletedAsync(
        IReadOnlyCollection<Core.Models.V4.IV4Record> records, IReadOnlyCollection<StateSpan> spans,
        CancellationToken ct = default)
    {
        if (records.Count == 0 && spans.Count == 0)
            return;

        await using var db = await contexts.CreateAsync(ct);
        var deletedAt = DateTime.UtcNow;
        foreach (var record in records)
            DecomposedTreatmentTables.Of(record).AddDeleted(db, record, deletedAt);
        foreach (var span in spans)
        {
            var row = StateSpanMapper.ToEntity(span);
            row.DeletedAt = deletedAt;
            db.StateSpans.Add(row);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A row the user deleted stays under the client id and its tombstone is copied onto every
    /// equivalent, because <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/> blocks
    /// only the id a tombstone carries; left in place, it still reaches an equivalent a later publish
    /// delivers. The rows and holds are read before the transaction the moves and copies commit in.
    /// </remarks>
    public async Task<ClientIdRekey> RekeyClientIdsAsync(
        string source, IReadOnlyList<ClientIdEquivalent> equivalents, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var groups = new Dictionary<string, List<ClientIdEquivalent>>(StringComparer.Ordinal);
        foreach (var equivalent in equivalents)
        {
            if (!groups.TryGetValue(equivalent.ClientId, out var group))
                groups[equivalent.ClientId] = group = [];
            group.Add(equivalent);
        }

        if (groups.Count == 0)
            return new ClientIdRekey(0, Written: false);

        string[] clientIds = [.. groups.Keys];
        var stored = new List<(StoredLegacyId Row, bool Live)>();
        foreach (var table in DecomposedTreatmentTables.All)
            stored.AddRange(await table.ClientIdRowsAsync(db, source, clientIds, ct));
        stored.AddRange((await StateSpansFromSource(db, source, clientIds).AsNoTracking()
                .Select(s => new { Key = s.LegacyId!, s.StartTimestamp, Live = s.DeletedAt == null })
                .ToListAsync(ct))
            .Select(s => (new StoredLegacyId(s.Key, s.StartTimestamp), s.Live)));
        if (stored.Count == 0)
            return new ClientIdRekey(0, Written: false);

        var (held, _) = await HeldAsync(db, equivalents.Select(e => e.Id).ToHashSet(), ct);
        var moves = new List<(string From, string To)>();
        var copies = new List<(string From, string[] To)>();
        foreach (var rows in stored.GroupBy(r => r.Row.LegacyId, StringComparer.Ordinal))
        {
            var group = groups[rows.Key];
            if (!rows.Any(r => r.Live))
            {
                string[] unheld = [.. group.Select(e => e.Id).Where(id => !held.Contains(id)).Distinct()];
                if (unheld.Length > 0)
                    copies.Add((rows.Key, unheld));
                continue;
            }

            var storedAt = rows.Select(r => r.Row.At.ToMills()).ToHashSet();
            var target = group.FirstOrDefault(e => storedAt.Contains(e.Mills)) ?? group[0];
            if (!held.Contains(target.Id))
                moves.Add((rows.Key, target.Id));
        }

        if (moves.Count == 0 && copies.Count == 0)
            return new ClientIdRekey(0, Written: false);

        var rekeyed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var moved = 0;
            foreach (var (from, to) in moves)
            {
                foreach (var table in DecomposedTreatmentTables.All)
                    moved += await table.RekeyAsync(db, source, from, to, ct);

                moved += await StateSpansFromSource(db, source, [from])
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LegacyId, to), ct);
            }

            foreach (var (from, to) in copies)
            {
                foreach (var table in DecomposedTreatmentTables.All)
                    await table.AddTombstoneCopiesAsync(db, source, from, to, ct);

                var spanTombstones = await StateSpansFromSource(db, source, [from]).AsNoTracking()
                    .Where(s => s.DeletedAt != null).ToListAsync(ct);
                AddTombstoneCopies(db, spanTombstones, to, (s, id) => s.LegacyId = id);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return moved;
        });

        return new ClientIdRekey(rekeyed, Written: true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StoredFingerprint>>> GetFingerprintsFromSourceAsync(
        string source, IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        string[] ids = [.. legacyIds];
        var stored = new List<(string LegacyId, StoredFingerprint Fingerprint)>();
        foreach (var table in DecomposedTreatmentTables.All)
        {
            stored.AddRange((await table.FingerprintsFromSourceAsync(db, source, ids, ct))
                .Select(row => (row.LegacyId, new StoredFingerprint(row.Fingerprint, OnSpan: false))));
        }

        stored.AddRange((await TreatmentSpansFromSource(db, source, ids).AsNoTracking()
                .Select(s => new { LegacyId = s.LegacyId!, s.UpstreamFingerprint })
                .ToListAsync(ct))
            .Select(span => (span.LegacyId, new StoredFingerprint(span.UpstreamFingerprint, OnSpan: true))));

        return stored.GroupBy(r => r.LegacyId, StringComparer.Ordinal).ToDictionary(
            g => g.Key, IReadOnlyList<StoredFingerprint> (g) => [.. g.Select(r => r.Fingerprint)], StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task StampUnfingerprintedAsync(
        string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct = default)
    {
        if (fingerprints.Count == 0)
            return;

        string[] ids = [.. fingerprints.Keys];
        string[] values = [.. ids.Select(id => fingerprints[id])];
        await using var db = await contexts.CreateAsync(ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.PlanForEachArrayAsync(ids.Length, ct);
            foreach (var table in DecomposedTreatmentTables.All)
                await table.StampUnfingerprintedAsync(db, source, ids, values, ct);
            await TreatmentSpansFromSource(db, source, ids)
                .Where(s => s.UpstreamFingerprint == null)
                .ExecuteUpdateAsync(u => u.SetProperty(
                    s => s.UpstreamFingerprint, s => values[Array.IndexOf(ids, s.LegacyId!)]), ct);
            await transaction.CommitAsync(ct);
        });
    }

    /// <summary>Serialises the stamps of a tenant, each of which reads what its rows store and writes it on.</summary>
    private const int SentStampLockClass = 0x4E57_4253;

    /// <inheritdoc />
    /// <remarks>
    /// Runs with the member history clamp lifted: the record was sent because its writer may write it,
    /// and a clamped member's edit of a record dated before the window would otherwise stamp nothing,
    /// leaving it reading as never sent.
    /// </remarks>
    public Task RecordSentAsync(IReadOnlyList<WriteBackSentRecord> records, CancellationToken ct = default)
        => StampAsync(records, WriteBackFingerprint.Accepted, ct);

    /// <inheritdoc />
    /// <remarks>Runs with the member history clamp lifted, as <see cref="RecordSentAsync"/> does.</remarks>
    public Task RecordSendingAsync(IReadOnlyList<WriteBackSentRecord> records, CancellationToken ct = default)
        => StampAsync(records, WriteBackFingerprint.Sending, ct);

    /// <summary>
    /// Sets each live row of <paramref name="records"/> to <paramref name="stamp"/> of what it stores
    /// and its record's documents. Rows are read and written under a lock on the tenant, so two sends
    /// of one record cannot each write over what the other added.
    /// </summary>
    private async Task StampAsync(
        IReadOnlyList<WriteBackSentRecord> records,
        Func<string?, IReadOnlyList<string>, DateTimeOffset, string?> stamp,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return;

        await using var db = await contexts.CreateAsync(ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.WriteWithHistoryClampLiftedAsync(
                async token =>
                {
                    await db.LockForTransactionAsync(SentStampLockClass, db.TenantId, token);
                    var now = time.GetUtcNow();

                    var byLegacyId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                    var byRow = new Dictionary<Guid, IReadOnlyList<string>>();
                    foreach (var record in records)
                    {
                        if (record.LegacyId is { Length: > 0 } legacyId)
                            byLegacyId[legacyId] = record.Documents;
                        else if (record.RecordId is { } recordId)
                            byRow[recordId] = record.Documents;
                    }

                    var byCorrelation = new Dictionary<Guid, IReadOnlyList<string>>();
                    if (byRow.Count > 0)
                    {
                        Guid[] rowIds = [.. byRow.Keys];
                        foreach (var table in DecomposedTreatmentTables.All)
                        {
                            foreach (var (id, correlationId) in await table.CorrelationsAsync(db, rowIds, token))
                                byCorrelation[correlationId] = byRow[id];
                        }
                    }

                    foreach (var table in DecomposedTreatmentTables.All)
                    {
                        var stamped = new Dictionary<Guid, string>();
                        foreach (var row in await table.SentRowsAsync(db, byLegacyId, byRow, byCorrelation, token))
                        {
                            if (stamp(row.Stored, row.Documents, now) is { } value && value != row.Stored)
                                stamped[row.Id] = value;
                        }

                        await table.StampSentAsync(db, stamped, token);
                    }

                    return true;
                },
                ct);
            await transaction.CommitAsync(ct);
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads with the member history clamp lifted, so a clamped caller's record dated before the window
    /// does not read as never sent. It returns only ids the caller named, and its answer only feeds a
    /// decision made inside Nocturne: returned to a request, it would tell a clamped member of a record
    /// dated before the window.
    /// </remarks>
    public async Task<IReadOnlySet<string>> GetSentUpstreamAsync(IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        var sent = new HashSet<string>(StringComparer.Ordinal);
        if (legacyIds.Count == 0)
            return sent;

        await using var db = await contexts.CreateAsync(ct);
        string[] ids = [.. legacyIds];
        return await db.WriteWithHistoryClampLiftedAsync(
            async token =>
            {
                foreach (var table in DecomposedTreatmentTables.All)
                    sent.UnionWith(await table.SentUpstreamAsync(db, ids, token));
                return sent;
            },
            ct);
    }

    /// <summary>Stages a user tombstone like each of <paramref name="tombstones"/> under each of <paramref name="keys"/>.</summary>
    internal static void AddTombstoneCopies<T>(
        NocturneDbContext context, IEnumerable<T> tombstones, string[] keys, Action<T, string> setKey)
        where T : class, IIdentified
    {
        foreach (var row in tombstones)
        {
            foreach (var key in keys)
            {
                var copy = (T)context.Entry(row).CurrentValues.ToObject();
                copy.Id = Guid.CreateVersion7();
                setKey(copy, key);
                context.Add(copy);
                context.Entry(copy).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = true;
            }
        }
    }

    private static async Task<HeldLegacyIds> HeldAsync(NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct)
    {
        var rows = new List<(string Key, bool Live)>();
        foreach (var table in DecomposedTreatmentTables.All)
            rows.AddRange(await table.BlockingAsync(db, legacyIds, ct));

        var spans = await db.GetBlockingSpanLegacyIdsAsync(legacyIds, ct);
        rows.AddRange(spans.Held.Select(id => (id, !spans.DeletedByUser.Contains(id))));

        var blocks = RecreationBlocks<string>.From(rows);
        return new HeldLegacyIds(blocks.Held, blocks.DeletedByUser);
    }

    /// <summary>The live spans of <see cref="TreatmentSpanCategories"/> <paramref name="source"/> stored under <paramref name="legacyIds"/>.</summary>
    private static IQueryable<StateSpanEntity> TreatmentSpansFromSource(
        NocturneDbContext db, string source, string[] legacyIds)
    {
        string[] categories = [.. TreatmentSpanCategories];
        return db.StateSpans.Where(s => s.Source == source
            && s.LegacyId != null && legacyIds.Contains(s.LegacyId) && categories.Contains(s.Category));
    }

    private static IQueryable<StateSpanEntity> StateSpansFromSource(
        NocturneDbContext db, string source, string[] legacyIds) =>
        db.StateSpans.IncludingDeleted()
            .Where(s => s.Source == source
                     && s.LegacyId != null && legacyIds.Contains(s.LegacyId))
            .WhereBlocksSpanRecreation();
}
