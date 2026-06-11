using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Services;

namespace Nocturne.Connectors.Tandem;

public class TandemConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Tandem";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<TandemConnectorConfiguration, TandemConnectorService, TandemAuthTokenProvider>(
            configuration,
            new TandemConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<TandemAuthTokenProvider>();
        services.AddConnectorSyncExecutor<TandemSyncExecutor>();
    }

    private sealed class TandemConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public TandemConnectorOptions()
        {
            ConnectorName = "Tandem";
            // Long-running history fetches; allow generous timeouts and resilience policies.
            Timeout = TimeSpan.FromMinutes(5);
            ConnectTimeout = TimeSpan.FromSeconds(15);
            AddResilience = true;
        }
    }
}

public class TandemSyncExecutor
    : ConnectorSyncExecutor<TandemConnectorService, TandemConnectorConfiguration>
{
    public override string ConnectorId => "tandem";

    protected override string ConnectorName => "Tandem";
}
