using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The single origin-gated fan-out used by every V4 write chokepoint — <see cref="V4RepositoryBase{TModel,TEntity}"/>
/// and the two off-base repositories (TempBasal, BasalInjection) that can't inherit it. Broadcasts only
/// for <see cref="WriteOrigin.Live"/> writes (backfill stays silent) and no-ops when no broadcaster is wired.
/// </summary>
internal static class V4RecordBroadcast
{
    public static async Task RaiseAsync<TModel>(
        IV4RecordBroadcaster<TModel>? broadcaster,
        IReadOnlyList<TModel> created,
        IReadOnlyList<TModel> updated,
        IReadOnlyList<Guid> deletedIds,
        WriteOrigin origin,
        CancellationToken ct) where TModel : class
    {
        if (origin != WriteOrigin.Live || broadcaster is null)
            return;
        if (created.Count > 0) await broadcaster.BroadcastCreatedAsync(created, ct);
        if (updated.Count > 0) await broadcaster.BroadcastUpdatedAsync(updated, ct);
        if (deletedIds.Count > 0) await broadcaster.BroadcastDeletedAsync(deletedIds, ct);
    }
}
