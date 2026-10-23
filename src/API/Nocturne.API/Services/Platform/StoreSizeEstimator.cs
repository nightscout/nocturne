using System.Collections.Concurrent;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// Runs the store size count that concurrent readers of one tenant and partition share. A singleton,
/// so its <paramref name="contexts"/> is resolved from the root: the count reads as no reader, with
/// the tenant pinned from a captured value, and depends on no request's scope, execution context or
/// token. The statement timeout bounds it.
/// </summary>
public sealed class StoreSizeEstimator(INocturneDbContextFactory contexts)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DbStats>>> _inFlight = new();

    public Task<DbStats> SharedAsync(Guid tenantId, string partition)
    {
        var flightKey = $"{tenantId:D}:{partition}";
        Lazy<Task<DbStats>> flight = null!;
        flight = new Lazy<Task<DbStats>>(() => StartDetached(async () =>
        {
            try
            {
                await using var db = await contexts.CreateForTenantAsync(tenantId, CancellationToken.None);
                return await StoreSizeReader.EstimateAsync(db, CancellationToken.None);
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<DbStats>>>(flightKey, flight));
            }
        }));

        return _inFlight.GetOrAdd(flightKey, flight).Value;
    }

    private static Task<DbStats> StartDetached(Func<Task<DbStats>> work)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(work);
    }
}
