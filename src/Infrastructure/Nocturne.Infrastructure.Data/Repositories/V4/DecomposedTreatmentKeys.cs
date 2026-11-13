using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// <see cref="IDecomposedTreatmentKeys"/> over <see cref="DecomposedTreatmentTables"/> and the state
/// spans of overrides and temporary targets, one context of the scope's reader per call.
/// </summary>
public sealed class DecomposedTreatmentKeys(INocturneDbContextFactory contexts) : IDecomposedTreatmentKeys
{
    /// <inheritdoc />
    public async Task<HeldLegacyIds> GetHeldLegacyIdsAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await HeldAsync(db, [.. legacyIds], ct);
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
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string?>>> GetFingerprintsFromSourceAsync(
        string source, IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        string[] ids = [.. legacyIds];
        var stamped = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        foreach (var table in DecomposedTreatmentTables.All)
        {
            foreach (var (legacyId, fingerprint) in await table.FingerprintsFromSourceAsync(db, source, ids, ct))
            {
                if (!stamped.TryGetValue(legacyId, out var rowFingerprints))
                    stamped[legacyId] = rowFingerprints = [];
                rowFingerprints.Add(fingerprint);
            }
        }

        return stamped.ToDictionary(p => p.Key, IReadOnlyList<string?> (p) => p.Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task StampUnfingerprintedAsync(
        string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var table in DecomposedTreatmentTables.All)
                await table.StampUnfingerprintedAsync(db, source, fingerprints, ct);
            await transaction.CommitAsync(ct);
        });
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

    private static IQueryable<StateSpanEntity> StateSpansFromSource(
        NocturneDbContext db, string source, string[] legacyIds) =>
        db.StateSpans.IncludingDeleted()
            .Where(s => s.Source == source
                     && s.LegacyId != null && legacyIds.Contains(s.LegacyId))
            .WhereBlocksSpanRecreation();
}
