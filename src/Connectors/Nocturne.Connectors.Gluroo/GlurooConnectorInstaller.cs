using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Gluroo.Configurations;
using Nocturne.Connectors.Gluroo.Services;

namespace Nocturne.Connectors.Gluroo;

public class GlurooConnectorInstaller()
    : TenantUrlConnectorInstaller<GlurooConnectorConfiguration, GlurooConnectorService>(
        new ConnectorOptions { ConnectorName = "Gluroo" },
        config => config.Url);
