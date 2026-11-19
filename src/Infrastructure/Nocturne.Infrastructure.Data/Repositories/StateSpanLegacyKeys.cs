using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// The legacy-id lookups of <see cref="ILegacyKeyedRepository{TRecord}"/> a pull resolves a
/// written-back copy through, over the state spans of <paramref name="categories"/>: those a legacy
/// treatment is stored as, and read back and written back as.
/// </summary>
public sealed class StateSpanLegacyKeys(INocturneDbContextFactory contexts, IReadOnlyCollection<string> categories)
{
    private readonly List<string> _categories = [.. categories];

    private IQueryable<StateSpanEntity> Rows(NocturneDbContext db) =>
        db.StateSpans.IncludingDeleted().AsNoTracking().Where(s => _categories.Contains(s.Category));

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.GetHeldLegacyIdsAsync" />
    public async Task<IReadOnlySet<string>> GetHeldLegacyIdsAsync(IReadOnlyCollection<string> legacyIds, CancellationToken ct)
    {
        if (legacyIds.Count == 0)
            return RecreationBlocks<string>.None.Held;

        await using var db = await contexts.CreateAsync(ct);
        var ids = legacyIds.Distinct(StringComparer.Ordinal).ToList();
        return (await Rows(db)
                .Where(s => s.LegacyId != null && ids.Contains(s.LegacyId))
                .WhereBlocksSpanRecreation()
                .Select(s => s.LegacyId!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.GetLegacyIdsWriteBackMaySendAsync" />
    public async Task<IEnumerable<WriteBackSendable>> GetLegacyIdsWriteBackMaySendAsync(
        IReadOnlyCollection<string> legacyIds, string skippedSource, CancellationToken ct)
    {
        if (legacyIds.Count == 0)
            return [];

        await using var db = await contexts.CreateAsync(ct);
        var ids = legacyIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = await Rows(db)
            .Where(s => s.LegacyId != null && ids.Contains(s.LegacyId))
            .WhereBlocksSpanRecreation()
            .Select(s => new LegacyWireIdLookups.WriteBackCandidate(s.LegacyId!, s.Id, s.Source, s.DeletedAt, s.WrittenLive))
            .ToListAsync(ct);
        return LegacyWireIdLookups.WriteBackMaySend(rows, skippedSource)
            .Select(r => new WriteBackSendable(r.LegacyId, r.Source, null, r.DeletedAt == null))
            .ToList();
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.ResolveUuidLegacyIdsAsync" />
    public async Task<IEnumerable<WireLegacyId>> ResolveUuidLegacyIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await LegacyWireIdLookups.ResolveUuidLegacyIdsAsync(Rows(db), ids, ct);
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.ResolveHashedLegacyIdsAsync" />
    /// <remarks>A span is timed by its start, which its treatment's <c>created_at</c> carries.</remarks>
    public async Task<IEnumerable<WireLegacyId>> ResolveHashedLegacyIdsAsync(
        IReadOnlyCollection<string> ids, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var windowLegacyIds = Rows(db)
            .Where(s => s.StartTimestamp >= from && s.StartTimestamp <= to && s.LegacyId != null)
            .Select(s => s.LegacyId!)
            .Distinct();
        return await LegacyWireIdLookups.ResolveWindowLegacyIdsAsync(windowLegacyIds, ids, LegacyWireIdLookups.HashedWireId, ct);
    }

    /// <inheritdoc cref="ILegacyKeyedRepository{TRecord}.ResolveKeyedOwnIdsAsync" />
    public async Task<IEnumerable<WireLegacyId>> ResolveKeyedOwnIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await LegacyWireIdLookups.ResolveKeyedOwnIdsAsync(Rows(db), ids, ct);
    }
}
