using System.Reflection;
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
    /// <remarks>
    ///     Read without inheritance: a config declared by subclassing another connector's config
    ///     (Gluroo extends Nightscout) would otherwise answer the parent's id, registering a second
    ///     executor under it and leaving a trigger to pick whichever DI enumerated first.
    /// </remarks>
    public string ConnectorId { get; } =
        typeof(TConfig).GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false)?.ConnectorId
        ?? throw new InvalidOperationException(
            $"{typeof(TConfig).Name} declares no {nameof(ConnectorRegistrationAttribute)} of its own, " +
            "so no sync trigger could ever dispatch to it.");

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
