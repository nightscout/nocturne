using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Gluroo.Services;
using Nocturne.Connectors.Nightscout.Services;

namespace Nocturne.Connectors.Gluroo;

public class GlurooConnectorInstaller()
    : TenantUrlConnectorInstaller<GlurooConnectorConfiguration, GlurooConnectorService>(
        new ConnectorOptions(),
        config => config.Url)
{
    protected override void InstallAdditional(IServiceCollection services, GlurooConnectorConfiguration config) =>
        services.TryAddSingleton<NightscoutLookups>();
}
