using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Installs a connector whose registration is entirely standard — configuration, service,
///     token provider and the generic sync executor — so that all it has to state is
///     <paramref name="options"/> and its three types. A connector that has no token provider and
///     takes its base URL from per-tenant configuration derives from
///     <see cref="TenantUrlConnectorInstaller{TConfig,TService}"/>; one that needs anything else
///     again (a hand-rolled client, extra services) implements <see cref="IConnectorInstaller"/>
///     directly and calls the pieces itself.
/// </summary>
/// <typeparam name="TConfig">Configuration type</typeparam>
/// <typeparam name="TService">Connector service type</typeparam>
/// <typeparam name="TTokenProvider">Token provider type</typeparam>
/// <param name="options">Connector options</param>
public abstract class ConnectorInstaller<TConfig, TService, TTokenProvider>(ConnectorOptions options)
    : IConnectorInstaller
    where TConfig : BaseConnectorConfiguration, new()
    where TService : class, IConnectorService<TConfig>
    where TTokenProvider : class
{
    /// <inheritdoc />
    public void Install(IServiceCollection services, IConfiguration configuration) =>
        services.AddConnector<TConfig, TService, TTokenProvider>(configuration, options);
}
