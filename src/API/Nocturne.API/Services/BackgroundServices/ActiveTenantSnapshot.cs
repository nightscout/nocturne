using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>A tenant a background sweep visits.</summary>
public sealed record ActiveTenant(Guid Id, string Slug, string DisplayName);

/// <summary>
/// The active tenants the background sweeps visit, read at most once per <see cref="Ttl"/> for all of
/// them. One read per <see cref="ConnectorBackgroundService{TConfig}"/> per tick would cost a query and
/// an unleased connection each, outside the <see cref="ConnectorSyncBudget"/>.
/// </summary>
/// <remarks>
/// A tenant created or deactivated is seen by the sweeps within <see cref="Ttl"/> plus one refresh. A
/// failed refresh serves the last good list for another <see cref="Ttl"/>, so an outage costs one query
/// per window rather than one per poller tick; with no list yet, the failure reaches the caller.
/// </remarks>
public sealed class ActiveTenantSnapshot(
    IDbContextFactory<NocturneDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<ActiveTenantSnapshot> logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private IReadOnlyList<ActiveTenant>? _tenants;
    private DateTimeOffset _expiresAt;
    private Task<IReadOnlyList<ActiveTenant>>? _refresh;

    /// <summary>
    /// The active tenants as of the last refresh. Past <see cref="Ttl"/> the last list is returned at
    /// once while one refresh runs behind it; only a read with no list yet waits, sharing any refresh
    /// in flight. <paramref name="cancellationToken"/> abandons that wait, not the shared query.
    /// </summary>
    public Task<IReadOnlyList<ActiveTenant>> GetAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<ActiveTenant>> refresh;
        lock (_gate)
        {
            var tenants = _tenants;
            if (tenants is not null && timeProvider.GetUtcNow() < _expiresAt)
                return Task.FromResult(tenants);

            if (_refresh is null || _refresh.IsCompleted)
                _refresh = RefreshAsync();

            if (tenants is not null)
                return Task.FromResult(tenants);

            refresh = _refresh;
        }

        return refresh.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ActiveTenant>> RefreshAsync()
    {
        await Task.Yield();

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var tenants = await context.Tenants.AsNoTracking()
                .Where(t => t.IsActive)
                .Select(t => new ActiveTenant(t.Id, t.Slug, t.DisplayName))
                .ToListAsync();

            lock (_gate)
            {
                _tenants = tenants;
                _expiresAt = timeProvider.GetUtcNow() + Ttl;
            }

            return tenants;
        }
        catch (Exception ex)
        {
            IReadOnlyList<ActiveTenant>? last;
            lock (_gate)
            {
                last = _tenants;
                if (last is not null)
                    _expiresAt = timeProvider.GetUtcNow() + Ttl;
            }

            if (last is null)
                throw;

            logger.LogWarning(
                ex,
                "Failed to refresh the active tenant list; serving the last one read ({TenantCount} tenants)",
                last.Count);

            return last;
        }
    }
}
