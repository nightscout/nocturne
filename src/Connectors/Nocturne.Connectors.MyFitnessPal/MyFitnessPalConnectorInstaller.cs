using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;

namespace Nocturne.Connectors.MyFitnessPal;

public class MyFitnessPalConnectorInstaller : IConnectorInstaller
{
    public string ConnectorName => "MyFitnessPal";

    public void Install(IServiceCollection services, IConfiguration configuration)
    {
        var config = services.AddConnector<MyFitnessPalConnectorConfiguration, MyFitnessPalConnectorService, MyFitnessPalAuthTokenProvider>(
            configuration,
            new MyFitnessPalConnectorOptions());

        if (config == null)
            return;

        services.AddConnectorTokenProvider<MyFitnessPalAuthTokenProvider>();
        services.AddConnectorSyncExecutor<MyFitnessPalSyncExecutor>();
    }

    private sealed class MyFitnessPalConnectorOptions : ConnectorOptions
    {
        [SetsRequiredMembers]
        public MyFitnessPalConnectorOptions()
        {
            ConnectorName = "MyFitnessPal";
            // Fixed hosts rather than a region mapping; both call sites use absolute URLs.
            DefaultServer = MyFitnessPalConstants.Servers.GraphQl;
            UserAgent = $"MyFitnessPal/{MyFitnessPalConstants.AppVersion} Android";
        }
    }
}

public class MyFitnessPalSyncExecutor
    : ConnectorSyncExecutor<MyFitnessPalConnectorService, MyFitnessPalConnectorConfiguration>
{
    public override string ConnectorId => "myfitnesspal";

    protected override string ConnectorName => "MyFitnessPal";
}
