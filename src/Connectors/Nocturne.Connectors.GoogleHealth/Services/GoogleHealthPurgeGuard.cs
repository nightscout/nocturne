using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.GoogleHealth.Services;

/// <summary>
///     Holds the tenant's sync gate, which every Google Health sync takes before it writes. The
///     stored grant alone decides whether the connector syncs, so disabling it would not stop one.
/// </summary>
public sealed class GoogleHealthPurgeGuard(
    IGoogleHealthSyncCoordinator coordinator,
    ITenantAccessor tenant,
    TimeSpan? gateTimeout = null) : IConnectorPurgeGuard
{
    private static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromSeconds(5);

    public string ConnectorName => GoogleHealthConnectorService.ConnectorName;

    public Task<IAsyncDisposable?> PauseSyncsAsync(CancellationToken ct) =>
        coordinator.AcquireAsync(tenant.TenantId, ct, gateTimeout ?? DefaultGateTimeout);
}
