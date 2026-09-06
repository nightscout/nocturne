using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;

namespace Nocturne.Connectors.MyFitnessPal;

public class MyFitnessPalConnectorInstaller()
    : ConnectorInstaller<MyFitnessPalConnectorConfiguration, MyFitnessPalConnectorService, MyFitnessPalAuthTokenProvider>(
        new ConnectorOptions
        {
            ConnectorName = "MyFitnessPal",
            // Fixed hosts rather than a region mapping; both call sites use absolute URLs.
            DefaultServer = MyFitnessPalConstants.Servers.GraphQl,
            UserAgent = $"MyFitnessPal/{MyFitnessPalConstants.AppVersion} Android",
        });
