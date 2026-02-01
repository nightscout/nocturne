using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Configurations.Constants;
using Nocturne.Connectors.FreeStyle.Services;

namespace Nocturne.Connectors.FreeStyle;

public static class ServiceCollectionExtensions
{
    public static void AddLibreLinkUpConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var libreConfig = services.AddConnectorConfiguration<LibreLinkUpConnectorConfiguration>(
            configuration,
            "LibreLinkUp"
        );
        if (!libreConfig.Enabled)
            return;

        var server = ConnectorServerResolver.Resolve(
            libreConfig.LibreRegion,
            new Dictionary<string, string>
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
            },
            LibreLinkUpConstants.Endpoints.Eu
        );

        services.AddHttpClient<LibreConnectorService>().ConfigureLibreLinkUpClient(server);
        services.AddHttpClient<LibreLinkAuthTokenProvider>().ConfigureLibreLinkUpClient(server);
    }
}
