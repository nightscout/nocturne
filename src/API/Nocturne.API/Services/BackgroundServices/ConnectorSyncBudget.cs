using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.Connectors.Core.Extensions;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// The process-wide bound on how many tenant syncs may run at once, and the phase offsets that keep
/// the connector pollers' ticks apart. One instance is shared by every
/// <see cref="ConnectorBackgroundService{TConfig}"/>.
/// </summary>
/// <remarks>
/// Each poller fans out over every active tenant with its own per-service cap, and every tenant sync
/// opens its own DI scope and at least one database connection. With one poller per installed
/// connector and every poller on the same clock, the connections in flight at a tick peak at
/// <c>connectors × per-service cap</c> regardless of tenant count — enough to exhaust a Postgres
/// <c>max_connections</c> of 100 and refuse the request path with "too many clients already". The
/// lease bounds that total to <see cref="Slots"/> whatever the connector count; the stagger spreads
/// the pollers' first ticks evenly across the poll interval so they rarely contend for it.
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
    private int _pollersStarted;

    /// <param name="pollerCount">
    /// How many pollers share the budget; the poll interval is divided into this many phases.
    /// </param>
    public ConnectorSyncBudget(int slots = DefaultSlots, int pollerCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pollerCount, 1);

        Slots = slots;
        PollerCount = pollerCount;
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>
    /// Resolves the budget a host runs on: <see cref="SlotsKey"/> from the shared connector settings,
    /// else <see cref="DefaultSlots"/>, over the pollers already registered in
    /// <paramref name="services"/>.
    /// </summary>
    public static ConnectorSyncBudget FromConfiguration(IConfiguration configuration, IServiceCollection services) =>
        new(
            configuration.GlobalConnectorSettings.GetValue<int?>(SlotsKey) ?? DefaultSlots,
            Math.Max(1, CountPollers(services)));

    /// <summary>Hosted services registered in <paramref name="services"/> that are connector pollers.</summary>
    public static int CountPollers(IServiceCollection services) =>
        services.Count(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType is { } implementation
            && IsPoller(implementation));

    private static bool IsPoller(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ConnectorBackgroundService<>))
                return true;
        }

        return false;
    }

    public int Slots { get; }

    public int PollerCount { get; }

    /// <summary>Tenant syncs currently holding a slot.</summary>
    public int InFlight => Slots - _slots.CurrentCount;

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
    /// The additional startup delay for the next poller to start: the poll interval divided into
    /// <see cref="PollerCount"/> phases, handed out in start order. A poller beyond the count wraps
    /// onto the first phase.
    /// </summary>
    public TimeSpan NextStartupOffset(TimeSpan pollInterval)
    {
        var ordinal = (Interlocked.Increment(ref _pollersStarted) - 1) % PollerCount;
        return pollInterval * ordinal / PollerCount;
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
