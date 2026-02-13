using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;

namespace Nocturne.Connectors.Nightscout;

public static class ServiceCollectionExtensions
{
    public static void AddNightscoutConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var nightscoutConfig = services.AddConnectorConfiguration<NightscoutConnectorConfiguration>(
            configuration,
            "Nightscout");

        if (!nightscoutConfig.Enabled)
            return;

        // URL comes from user config (possibly loaded from DB at runtime),
        // so configure it at registration time only if already available.
        if (!string.IsNullOrEmpty(nightscoutConfig.Url))
            services.AddHttpClient<NightscoutConnectorService>()
                .ConfigureConnectorClient(nightscoutConfig.Url);
        else
            services.AddHttpClient<NightscoutConnectorService>();
    }
}
