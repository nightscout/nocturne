using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The batch-insert tail every bulk-create path shares: collapse the candidates to one record per
/// external identity, drop the identities the blocking lookup holds while counting the ones the user
/// deleted, and insert the survivors in chunks.
/// </summary>
public static class BulkInsertExtensions
{
    /// <summary>
    /// Inserts the candidates whose key no blocking row holds. Runs inside the caller's transaction
    /// and execution strategy; it opens and commits neither.
    /// </summary>
    /// <param name="candidates">The entities to deduplicate, filter, and insert.</param>
    /// <param name="key">
    /// The entity's external identity, or null/empty when it has none. A candidate with no key cannot
    /// be matched against another in the batch or against a stored row, so it is kept as its own
    /// record, matching the <c>LegacyId ?? Id</c> fallback the bulk paths rely on.
    /// </param>
    /// <param name="blocking">
    /// The stored-row lookup per <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>.
    /// Only keyed candidates are submitted.
    /// </param>
    /// <returns>The inserted entities and how many were withheld because the user had deleted them.</returns>
    public static async Task<(List<TEntity> Inserted, int SkippedDeleted)> InsertUnblockedAsync<TEntity, TKey>(
        this NocturneDbContext ctx,
        IEnumerable<TEntity> candidates,
        Func<TEntity, TKey?> key,
        Func<HashSet<TKey>, CancellationToken, Task<RecreationBlocks<TKey>>> blocking,
        CancellationToken ct = default)
        where TEntity : class
        where TKey : notnull
    {
        var toInsert = new List<TEntity>();
        var keys = new HashSet<TKey>();
        var seen = new HashSet<TKey>();
        foreach (var candidate in candidates)
        {
            if (key(candidate) is { } candidateKey && !(candidateKey is string s && s.Length == 0))
            {
                keys.Add(candidateKey);
                if (seen.Add(candidateKey))
                    toInsert.Add(candidate);
            }
            else
            {
                toInsert.Add(candidate);
            }
        }

        var skippedDeleted = 0;
        if (keys.Count > 0)
        {
            var blocked = await blocking(keys, ct);
            skippedDeleted = toInsert.Count(e => key(e) is { } k && blocked.DeletedByUser.Contains(k));
            toInsert = toInsert
                .Where(e => !(key(e) is { } k && blocked.Held.Contains(k)))
                .ToList();
        }

        const int batchSize = 500;
        foreach (var batch in toInsert.Chunk(batchSize))
        {
            ctx.Set<TEntity>().AddRange(batch);
            await ctx.SaveChangesAsync(ct);
            ctx.ChangeTracker.Clear();
        }

        return (toInsert, skippedDeleted);
    }
}
