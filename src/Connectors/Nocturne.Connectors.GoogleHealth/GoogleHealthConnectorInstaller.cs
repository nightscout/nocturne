using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;

namespace Nocturne.Connectors.GoogleHealth;

public sealed class GoogleHealthConnectorInstaller : IConnectorInstaller
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        services.AddConnectorConfiguration<GoogleHealthConnectorConfiguration>(
            configuration,
            GoogleHealthConnectorService.ConnectorName);
        services.AddOptions<GoogleHealthPlatformOptions>().Configure(options =>
        {
            options.ClientId = configuration[GoogleHealthPlatformOptions.ClientIdKey]?.Trim() ?? "";
            options.ClientSecret = configuration[GoogleHealthPlatformOptions.ClientSecretKey]?.Trim() ?? "";
            options.RedirectUri = configuration[GoogleHealthPlatformOptions.RedirectUriKey]?.Trim() ?? "";
        });
        services.AddSingleton<IConnectorServerResolver<GoogleHealthConnectorConfiguration>>(
            new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null));
        services.AddScoped<ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>>();
        services.AddScoped<IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>,
            GoogleHealthConfigurationLoader>();
        services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
        services.TryAddSingleton<IConnectorCacheInvalidator>(provider =>
            provider.GetRequiredService<IConnectorTokenCache>());

        services.AddHttpClient<GoogleHealthClient>(client =>
        {
            client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
        }).ConfigureConnectorClient(null, timeout: RequestTimeout);

        services.AddHttpClient<GoogleHealthAuthTokenProvider>()
            .ConfigureConnectorClient(null, timeout: RequestTimeout);
        services.AddConnectorTokenProvider<GoogleHealthAuthTokenProvider>();
        services.AddScoped<GoogleHealthSessionStore>();
        services.AddScoped<IConnectorPurgeGuard, GoogleHealthPurgeGuard>();

        services.AddHttpClient<GoogleHealthConnectorService>()
            .ConfigureConnectorClient(null, timeout: RequestTimeout);
        services.AddConnectorSyncExecutor<ConnectorSyncExecutor<
            GoogleHealthConnectorService,
            GoogleHealthConnectorConfiguration>>();
    }
}
