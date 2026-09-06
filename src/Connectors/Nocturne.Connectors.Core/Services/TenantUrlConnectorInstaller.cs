using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Installs a connector that reaches a base URL supplied per tenant rather than a server chosen
///     by region, and that authenticates without a token provider — configuration, an unmapped
///     <see cref="ConnectorServerResolver{TConfig}"/>, the configuration loader, the shared token
///     cache and the generic sync executor. The server-mapping members of
///     <paramref name="options"/> have no effect here; a connector that resolves a server by region
///     or holds a token provider derives from
///     <see cref="ConnectorInstaller{TConfig,TService,TTokenProvider}"/> instead.
/// </summary>
/// <typeparam name="TConfig">Configuration type</typeparam>
/// <typeparam name="TService">Connector service type</typeparam>
/// <param name="options">Connector options</param>
/// <param name="configuredUrl">Reads the connector's base URL off its configuration</param>
public abstract class TenantUrlConnectorInstaller<TConfig, TService>(
    ConnectorOptions options,
    Func<TConfig, string?> configuredUrl)
    : IConnectorInstaller
    where TConfig : BaseConnectorConfiguration, new()
    where TService : class, IConnectorService<TConfig>
{
    /// <inheritdoc />
    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnectorConfiguration<TConfig>(
            configuration,
            options.ConnectorName);

        InstallUnconditional(services, config);

        if (!config.Enabled)
            return;

        services.AddSingleton<IConnectorServerResolver<TConfig>>(
            new ConnectorServerResolver<TConfig>(null, null, null));
        services.AddScoped<IConnectorConfigurationLoader<TConfig>, ConnectorConfigurationLoader<TConfig>>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

        ConfigureClient(services.AddHttpClient<TService>(), config);

        services.AddConnectorSyncExecutor<ConnectorSyncExecutor<TService, TConfig>>();

        InstallAdditional(services, config);
    }

    /// <summary>
    ///     Applies the connector's client configuration to <paramref name="builder"/>, baking in the
    ///     base URL when one is already known at startup.
    /// </summary>
    /// <remarks>
    ///     Every client goes through <c>ConfigureConnectorClient</c> whether or not there is a base
    ///     address to set, because that is what installs <see cref="LinkLocalGuardHandler"/> and
    ///     turns transport-level redirects off. The URL normally arrives from per-tenant
    ///     configuration at runtime, so at startup it is usually empty — the no-URL case is the one
    ///     that runs in production, and a bare <c>AddHttpClient</c> there left the guard absent for
    ///     exactly the connectors whose base URL a member supplies.
    /// </remarks>
    protected IHttpClientBuilder ConfigureClient(IHttpClientBuilder builder, TConfig config) =>
        builder.ConfigureConnectorClient(
            configuredUrl(config) is { Length: > 0 } url ? url : null,
            options.AdditionalHeaders,
            options.UserAgent,
            options.Timeout,
            options.ConnectTimeout,
            options.AddResilience);

    /// <summary>
    ///     Registrations a disabled connector still needs, made ahead of the enablement check.
    /// </summary>
    protected virtual void InstallUnconditional(IServiceCollection services, TConfig config)
    {
    }

    /// <summary>
    ///     Registrations beyond the standard set, made only for an enabled connector.
    /// </summary>
    protected virtual void InstallAdditional(IServiceCollection services, TConfig config)
    {
    }
}
