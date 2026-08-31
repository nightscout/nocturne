using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Runs one connector's sync: resolves the service, loads the tenant's config through
///     <see cref="IConnectorConfigurationLoader{TConfig}"/>, and delegates.
/// </summary>
public class ConnectorSyncExecutor<TService, TConfig> : IConnectorSyncExecutor
    where TService : class, IConnectorService<TConfig>
    where TConfig : BaseConnectorConfiguration
{
    /// <inheritdoc />
    /// <seealso cref="ConnectorRegistrationAttribute.DeclaredOn"/>
    public string ConnectorId { get; } =
        ConnectorRegistrationAttribute.DeclaredOn(typeof(TConfig)).ConnectorId;

    public async Task<SyncResult> ExecuteSyncAsync(
        IServiceProvider scopeProvider,
        SyncRequest request,
        CancellationToken ct,
        ISyncProgressReporter? progressReporter = null)
    {
        var loader = scopeProvider.GetRequiredService<IConnectorConfigurationLoader<TConfig>>();

        var config = await loader.LoadForTenantAsync(ct);

        var service = scopeProvider.GetRequiredService<TService>();
        return await service.SyncDataAsync(request, config, ct, progressReporter);
    }
}
