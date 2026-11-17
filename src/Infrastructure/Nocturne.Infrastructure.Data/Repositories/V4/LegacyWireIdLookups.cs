using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The lookups behind <see cref="ILegacyKeyedRepository{TRecord}"/>'s wire-id resolution, over any
/// legacy-keyed table, so the tables a legacy treatment decomposes into resolve a pulled copy alike
/// whichever repository serves them.
/// </summary>
public static class LegacyWireIdLookups
{
    /// <summary>
    /// One range per form a UUID legacy id with <paramref name="objectId"/> as its 24-hex prefix is stored
    /// in: dashed and dashless, each in lower and upper case.
    /// </summary>
    /// <remarks>
    /// Each range spans one case of one form, so it is correct under any collation that orders
    /// same-case hex digits in hex order (C, libc and ICU all do). The prefix match only re-checks the
    /// rows a range admitted.
    /// </remarks>
    public static IEnumerable<Expression<Func<TEntity, bool>>> UuidPrefixRanges<TEntity>(string objectId)
        where TEntity : class, ILegacyIdentified
    {
        var dashed = string.Join('-', objectId[..8], objectId[8..12], objectId[12..16], objectId[16..20], objectId[20..]);

        Expression<Func<TEntity, bool>> InRange(string prefix, string lowSuffix, string highSuffix, int length)
        {
            var (low, high) = (prefix + lowSuffix, prefix + highSuffix);
            return e => e.LegacyId != null
                && string.Compare(e.LegacyId, low) >= 0
                && string.Compare(e.LegacyId, high) <= 0
                && e.LegacyId.Length == length
                && e.LegacyId.StartsWith(prefix);
        }

        yield return InRange(dashed, "00000000", "ffffffff", 36);
        yield return InRange(dashed.ToUpperInvariant(), "00000000", "FFFFFFFF", 36);
        yield return InRange(objectId, "00000000", "ffffffff", 32);
        yield return InRange(objectId.ToUpperInvariant(), "00000000", "FFFFFFFF", 32);
    }

    /// <summary>Ids a lookup over <see cref="UuidPrefixRanges{TEntity}"/> reads in one statement.</summary>
    public const int IdsPerStatement = 50;

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.ResolveUuidLegacyIdsAsync" />
    /// <param name="rows">The table's rows, live or deleted, untracked.</param>
    public static async Task<IEnumerable<WireLegacyId>> ResolveUuidLegacyIdsAsync<TEntity>(
        IQueryable<TEntity> rows, IReadOnlyCollection<string> ids, CancellationToken ct)
        where TEntity : class, ILegacyIdentified
    {
        var prefixes = ids.Where(MongoObjectId.IsGuidPrefixShaped).Distinct(StringComparer.Ordinal).ToList();
        if (prefixes.Count == 0)
            return [];

        var legacyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in prefixes.Chunk(IdsPerStatement))
        {
            var ranges = chunk.SelectMany(UuidPrefixRanges<TEntity>).ToList();
            var row = ranges[0].Parameters[0];
            var any = ranges.Skip(1).Aggregate(ranges[0].Body, (matches, range) =>
                Expression.OrElse(matches, ReplacingExpressionVisitor.Replace(range.Parameters[0], row, range.Body)));
            legacyIds.UnionWith(await rows
                .Where(Expression.Lambda<Func<TEntity, bool>>(any, row))
                .Select(e => e.LegacyId!)
                .ToListAsync(ct));
        }

        var wanted = prefixes.ToHashSet(StringComparer.Ordinal);
        return legacyIds
            .Select(legacyId => (Wire: Guid.TryParse(legacyId, out var uuid) ? MongoObjectId.FromGuid(uuid) : null, LegacyId: legacyId))
            .Where(r => r.Wire is not null && wanted.Contains(r.Wire))
            .Select(r => new WireLegacyId(r.Wire!, r.LegacyId))
            .ToList();
    }

    /// <summary>
    /// The hash a treatment's legacy id goes upstream as (<see cref="MongoObjectId.Coerce"/>), or null
    /// for an ObjectId or a uuid, which go as themselves and as their prefix.
    /// </summary>
    public static string? HashedWireId(string legacyId) =>
        MongoObjectId.IsObjectId(legacyId) || Guid.TryParse(legacyId, out _) ? null : MongoObjectId.Coerce(legacyId);

    /// <summary>The legacy ids among <paramref name="windowLegacyIds"/> whose <paramref name="wireOf"/> is one of <paramref name="ids"/>.</summary>
    /// <param name="windowLegacyIds">The distinct legacy ids, live or deleted, of the rows timed within the window.</param>
    public static async Task<IEnumerable<WireLegacyId>> ResolveWindowLegacyIdsAsync(
        IQueryable<string> windowLegacyIds, IReadOnlyCollection<string> ids, Func<string, string?> wireOf, CancellationToken ct)
    {
        var hashes = ids.Where(MongoObjectId.IsObjectId).ToHashSet(StringComparer.Ordinal);
        if (hashes.Count == 0)
            return [];

        return (await windowLegacyIds.ToListAsync(ct))
            .Select(legacyId => (Wire: wireOf(legacyId), LegacyId: legacyId))
            .Where(r => r.Wire is not null && hashes.Contains(r.Wire))
            .Select(r => new WireLegacyId(r.Wire!, r.LegacyId))
            .ToList();
    }

    /// <summary>Bounds the OR chain <see cref="IdInAnyRange{TEntity}"/> builds for one query.</summary>
    public const int OwnIdRangesPerQuery = 200;

    /// <summary>The uuid range each of <paramref name="ids"/> names by <see cref="MongoObjectId.TryGetOwnIdRange"/>.</summary>
    public static List<(string Id, Guid Low, Guid High)> OwnIdRanges(IReadOnlyCollection<string> ids)
    {
        var ranges = new List<(string Id, Guid Low, Guid High)>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (MongoObjectId.TryGetOwnIdRange(id, out var low, out var high))
                ranges.Add((id, low, high));
        }

        return ranges;
    }

    /// <summary>Whether a row's own id falls in any of <paramref name="ranges"/>.</summary>
    public static Expression<Func<TEntity, bool>> IdInAnyRange<TEntity>(IEnumerable<(string Id, Guid Low, Guid High)> ranges)
        where TEntity : class, IIdentified
    {
        var row = Expression.Parameter(typeof(TEntity), "e");
        var id = Expression.Property(row, nameof(IIdentified.Id));
        Expression? any = null;
        foreach (var (_, low, high) in ranges)
        {
            // Closure members rather than constants, so EF sends the bounds as parameters.
            Expression<Func<Guid>> lowBound = () => low;
            Expression<Func<Guid>> highBound = () => high;
            var inRange = Expression.AndAlso(
                Expression.GreaterThanOrEqual(id, lowBound.Body),
                Expression.LessThanOrEqual(id, highBound.Body));
            any = any is null ? inRange : Expression.OrElse(any, inRange);
        }

        return Expression.Lambda<Func<TEntity, bool>>(any ?? Expression.Constant(false), row);
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.ResolveKeyedOwnIdsAsync" />
    /// <param name="rows">The table's rows, live or deleted, untracked.</param>
    public static async Task<IEnumerable<WireLegacyId>> ResolveKeyedOwnIdsAsync<TEntity>(
        IQueryable<TEntity> rows, IReadOnlyCollection<string> ids, CancellationToken ct)
        where TEntity : class, ILegacyIdentified
    {
        var ranges = OwnIdRanges(ids);
        if (ranges.Count == 0)
            return [];

        var resolved = new List<WireLegacyId>();
        foreach (var chunk in ranges.Chunk(OwnIdRangesPerQuery))
        {
            var found = await rows
                .Where(e => e.LegacyId != null)
                .Where(IdInAnyRange<TEntity>(chunk))
                .Select(e => new { e.Id, LegacyId = e.LegacyId! })
                .ToListAsync(ct);
            foreach (var row in found.OrderBy(r => r.Id))
                resolved.Add(new WireLegacyId(chunk.First(r => row.Id >= r.Low && row.Id <= r.High).Id, row.LegacyId));
        }

        return resolved;
    }

    /// <summary>A row holding a legacy id, as <see cref="WriteBackMaySend"/> weighs it.</summary>
    public sealed record WriteBackCandidate(string LegacyId, Guid Id, string? Source, DateTime? DeletedAt, bool WrittenLive);

    /// <summary>
    /// The rule of <see cref="ILegacyKeyedRepository{TRecord}.GetLegacyIdsWriteBackMaySendAsync"/>
    /// over <paramref name="rows"/>, the rows that block re-creating their legacy ids.
    /// </summary>
    public static List<WriteBackCandidate> WriteBackMaySend(IEnumerable<WriteBackCandidate> rows, string skippedSource) =>
        rows
            .GroupBy(r => r.LegacyId, StringComparer.Ordinal)
            .Select(g => g
                .OrderBy(r => r.DeletedAt == null ? 0 : 1)
                .ThenByDescending(r => r.DeletedAt)
                .ThenBy(r => r.Id)
                .First())
            .Where(r => r.WrittenLive && r.Source != skippedSource)
            .ToList();
}
