using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Services;

namespace Nocturne.Connectors.Twiist;

public class TwiistConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "Twiist";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<TwiistConnectorConfiguration, TwiistConnectorService, TwiistAuthTokenProvider>(
            configuration,
            new TwiistConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<TwiistAuthTokenProvider>();
        services.AddConnectorSyncExecutor<TwiistSyncExecutor>();
    }

    private sealed class TwiistConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public TwiistConnectorOptions()
        {
            ConnectorName = "Twiist";
        }
    }
}

public class TwiistSyncExecutor
    : ConnectorSyncExecutor<TwiistConnectorService, TwiistConnectorConfiguration>
{
    public override string ConnectorId => "twiist";

    protected override string ConnectorName => "Twiist";
}
