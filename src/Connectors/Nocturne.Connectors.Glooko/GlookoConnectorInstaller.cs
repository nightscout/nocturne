using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;

namespace Nocturne.Connectors.Glooko;

public class GlookoConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Glooko";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<GlookoConnectorConfiguration, GlookoConnectorService, GlookoAuthTokenProvider>(
            configuration,
            new GlookoConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<GlookoAuthTokenProvider>();
        services.AddConnectorSyncExecutor<GlookoSyncExecutor>();
    }

    private sealed class GlookoConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public GlookoConnectorOptions()
        {
            ConnectorName = "Glooko";
            Timeout = TimeSpan.FromMinutes(5);
            ConnectTimeout = TimeSpan.FromSeconds(15);
            AddResilience = true;
        }
    }
}

public class GlookoSyncExecutor : ConnectorSyncExecutor<GlookoConnectorService, GlookoConnectorConfiguration>
{
    public override string ConnectorId => "glooko";

    protected override string ConnectorName => "Glooko";
}
