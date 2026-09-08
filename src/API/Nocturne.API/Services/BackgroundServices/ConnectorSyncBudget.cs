using Microsoft.Extensions.Configuration;
using Nocturne.Connectors.Core.Extensions;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// The process-wide bound on how many tenant syncs may run at once, and the phase offsets that keep
/// the connector pollers' ticks apart. One instance is shared by every
/// <see cref="ConnectorBackgroundService{TConfig}"/>.
/// </summary>
/// <remarks>
/// Each poller fans out over every active tenant with its own per-service cap, and every tenant sync
/// opens its own DI scope and database connection. With one poller per installed connector and every
/// poller on the same clock, the connections in flight at a tick peak at
/// <c>connectors × per-service cap</c> regardless of tenant count — enough to exhaust a Postgres
/// <c>max_connections</c> of 100 and refuse the request path with "too many clients already". The
/// lease bounds that total to <see cref="MaxConcurrentTenantSyncs"/> whatever the connector count;
/// the stagger spreads the pollers across the poll interval so they rarely contend for it.
/// </remarks>
public sealed class ConnectorSyncBudget : IDisposable
{
    /// <summary>
    /// Configuration key under the shared connector defaults, <c>Connectors:Settings</c>, overriding
    /// <see cref="DefaultMaxConcurrentTenantSyncs"/>. Size it against the <c>MaxPoolSize</c> the
    /// request path has to share the server with, and remember a sync may hold more than one
    /// connection while its repositories write.
    /// </summary>
    public const string MaxConcurrentTenantSyncsKey = "MaxConcurrentTenantSyncs";

    /// <summary>
    /// A quarter of the default Npgsql pool of 100: thirteen pollers' lookup scopes plus this many
    /// syncs fit under it with room for requests, and the tenant work of a tick still clears in
    /// well under the interval.
    /// </summary>
    public const int DefaultMaxConcurrentTenantSyncs = 24;

    /// <summary>
    /// Gap between consecutive pollers' first ticks. Thirteen pollers on a one-minute interval spread
    /// across 48 seconds of it; the per-tenant interval check means a later first tick delays no
    /// tenant's sync by more than this.
    /// </summary>
    public static readonly TimeSpan DefaultStartupStagger = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _slots;
    private int _pollersStarted;

    public ConnectorSyncBudget(
        int maxConcurrentTenantSyncs = DefaultMaxConcurrentTenantSyncs,
        TimeSpan? startupStagger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentTenantSyncs, 1);

        MaxConcurrentTenantSyncs = maxConcurrentTenantSyncs;
        StartupStagger = startupStagger ?? DefaultStartupStagger;
        _slots = new SemaphoreSlim(maxConcurrentTenantSyncs, maxConcurrentTenantSyncs);
    }

    /// <summary>
    /// Resolves the budget a host runs on: <see cref="MaxConcurrentTenantSyncsKey"/> from the shared
    /// connector settings, else <see cref="DefaultMaxConcurrentTenantSyncs"/>.
    /// </summary>
    public static ConnectorSyncBudget FromConfiguration(IConfiguration configuration) =>
        new(configuration.GlobalConnectorSettings.GetValue<int?>(MaxConcurrentTenantSyncsKey)
            ?? DefaultMaxConcurrentTenantSyncs);

    public int MaxConcurrentTenantSyncs { get; }

    public TimeSpan StartupStagger { get; }

    /// <summary>Tenant syncs currently holding a slot.</summary>
    public int InFlight => MaxConcurrentTenantSyncs - _slots.CurrentCount;

    /// <summary>
    /// Waits for a slot and holds it until the returned lease is disposed. Acquire it around the whole
    /// of a tenant's sync, including the config load — that scope opens a connection too.
    /// </summary>
    public async ValueTask<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Lease(_slots);
    }

    /// <summary>
    /// The additional startup delay for the next poller to start: zero for the first, then one
    /// <see cref="StartupStagger"/> more for each after it.
    /// </summary>
    public TimeSpan NextStartupOffset() =>
        StartupStagger * (Interlocked.Increment(ref _pollersStarted) - 1);

    public void Dispose() => _slots.Dispose();

    /// <summary>A held slot; disposing returns it.</summary>
    public struct Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose()
        {
            Interlocked.Exchange(ref _slots, null)?.Release();
        }
    }
}
