using Microsoft.Extensions.Configuration;
using Nocturne.Connectors.Core.Extensions;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// The process-wide bound on how many scheduled tenant syncs may run at once. One instance is shared
/// by every <see cref="ConnectorBackgroundService{TConfig}"/>.
/// </summary>
/// <remarks>
/// Every tenant sync opens its own DI scope and at least one database connection. Without a shared
/// bound the connections in flight climb with the number of connectors syncing at once, enough to
/// exhaust a Postgres <c>max_connections</c> of 100 and refuse the request path with "too many
/// clients already". The lease bounds that total to <see cref="Slots"/> whatever the connector count.
/// </remarks>
public sealed class ConnectorSyncBudget
{
    /// <summary>
    /// Configuration key under the shared connector defaults, <c>Connectors:Settings</c>, overriding
    /// <see cref="DefaultSlots"/>. Size it against the <c>MaxPoolSize</c> the request path has to
    /// share the server with; a sync may hold more than one connection while its repositories write.
    /// </summary>
    public const string SlotsKey = "SyncSlots";

    /// <summary>A quarter of the default Npgsql pool of 100.</summary>
    public const int DefaultSlots = 24;

    private readonly SemaphoreSlim _slots;

    /// <param name="slots">Tenant syncs that may run at once across every poller; at least 1.</param>
    public ConnectorSyncBudget(int slots = DefaultSlots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);

        Slots = slots;
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>
    /// Resolves the budget a host runs on: <see cref="SlotsKey"/> from the shared connector settings,
    /// else <see cref="DefaultSlots"/>.
    /// </summary>
    public static ConnectorSyncBudget FromConfiguration(IConfiguration configuration) =>
        new(configuration.GlobalConnectorSettings.GetValue<int?>(SlotsKey) ?? DefaultSlots);

    public int Slots { get; }

    /// <summary>Tenant syncs currently holding a slot.</summary>
    public int InFlight => Slots - _slots.CurrentCount;

    /// <summary>
    /// Waits for a slot and holds it until the returned lease is disposed. Acquire it around the whole
    /// of a tenant's sync, including the config load, which opens a connection too.
    /// </summary>
    public async ValueTask<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Lease(_slots);
    }

    /// <summary>
    /// A held slot; disposing returns it. Dispose the instance the lease was acquired into — a copy
    /// is a second token over the same slot.
    /// </summary>
    public struct Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose()
        {
            Interlocked.Exchange(ref _slots, null)?.Release();
        }
    }
}
