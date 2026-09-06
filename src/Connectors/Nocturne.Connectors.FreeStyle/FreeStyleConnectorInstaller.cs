using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;

namespace Nocturne.Connectors.FreeStyle;

public class FreeStyleConnectorInstaller()
    : ConnectorInstaller<LibreLinkUpConnectorConfiguration, LibreConnectorService, LibreLinkAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "LibreLinkUp",
            DefaultServer = LibreLinkUpConstants.Endpoints.Eu,
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
            },
            GetServerRegion = config => ((LibreLinkUpConnectorConfiguration)config).Region,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Version"] = "4.16.0",
                ["Product"] = "llu.android"
            },
        });
