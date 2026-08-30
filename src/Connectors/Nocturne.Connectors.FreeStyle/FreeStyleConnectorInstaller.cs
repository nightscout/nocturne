using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;

namespace Nocturne.Connectors.FreeStyle;

public class FreeStyleConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "LibreLinkUp";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<LibreLinkUpConnectorConfiguration, LibreConnectorService, LibreLinkAuthTokenProvider>(
            configuration,
            new LibreLinkUpConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<LibreLinkAuthTokenProvider>();
        services.AddConnectorSyncExecutor<ConnectorSyncExecutor<LibreConnectorService, LibreLinkUpConnectorConfiguration>>();
    }

    private sealed class LibreLinkUpConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public LibreLinkUpConnectorOptions()
        {
            ConnectorName = "LibreLinkUp";
            DefaultServer = LibreLinkUpConstants.Endpoints.Eu;
            ServerMapping = new Dictionary<string, string>
            {
                ["AE"] = LibreLinkUpConstants.Endpoints.Ae,
                ["AP"] = LibreLinkUpConstants.Endpoints.Ap,
                ["AU"] = LibreLinkUpConstants.Endpoints.Au,
                ["CA"] = LibreLinkUpConstants.Endpoints.Ca,
                ["DE"] = LibreLinkUpConstants.Endpoints.De,
                ["EU"] = LibreLinkUpConstants.Endpoints.Eu,
                ["EU2"] = LibreLinkUpConstants.Endpoints.Eu2,
                ["FR"] = LibreLinkUpConstants.Endpoints.Fr,
                ["JP"] = LibreLinkUpConstants.Endpoints.Jp,
                ["US"] = LibreLinkUpConstants.Endpoints.Us
            };
            GetServerRegion = config => ((LibreLinkUpConnectorConfiguration)config).Region;
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Version"] = "4.16.0",
                ["Product"] = "llu.android"
            };
        }
    }
}
