using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Seeding;

/// <summary>
/// Runs <see cref="SampleDataLiveFeed"/> for every active tenant once a minute, so a reading appears
/// within a minute of its five-minute step.
/// </summary>
/// <remarks>
/// Every failure short of shutdown is logged and retried on the next tick: an exception escaping
/// <see cref="ExecuteAsync"/> stops the host, and the writes it drives can fail in many ways (the
/// database restarting, a treatment batch, a broadcast after commit).
/// </remarks>
public sealed class SampleDataLiveFeedService(
    IServiceProvider services,
    TimeProvider time,
    ILogger<SampleDataLiveFeedService> logger) : BackgroundService
{
    private const string AuditEndpoint = "service:sample-data-live-feed";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, SampleDataLiveCursor> _cursors = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await AdvanceAllAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task AdvanceAllAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantContext> tenants;
        try
        {
            await using var scope = TenantJobScope.Open(services, tenant: null, AuditEndpoint);
            tenants = (await scope.ServiceProvider.GetRequiredService<ITenantRepository>().ListAsync(activeOnly: true, ct))
                .Where(t => !t.IsDemo)
                .Select(t => new TenantContext(t.Id, t.Slug, t.DisplayName, t.IsActive, t.IsDemo))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not list the tenants whose sample data to continue");
            return;
        }

        foreach (var gone in _cursors.Keys.Except(tenants.Select(t => t.TenantId)).ToList())
            _cursors.Remove(gone);

        foreach (var tenant in tenants)
        {
            _cursors.Remove(tenant.TenantId, out var cursor);
            try
            {
                await using var scope = TenantJobScope.Open(services, tenant, AuditEndpoint);
                var next = await scope.ServiceProvider.GetRequiredService<SampleDataLiveFeed>().AdvanceAsync(cursor, ct);
                if (next is not null)
                    _cursors[tenant.TenantId] = next;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not continue the sample data of tenant {Slug}", tenant.Slug);
            }
        }
    }
}
