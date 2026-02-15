using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;

namespace Nocturne.Connectors.MyFitnessPal;

public static class ServiceCollectionExtensions
{
    public static void AddMyFitnessPalConnector(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var config = services.AddConnectorConfiguration<MyFitnessPalConnectorConfiguration>(
            configuration,
            "MyFitnessPal"
        );
        if (!config.Enabled)
            return;

        services
            .AddHttpClient<MyFitnessPalConnectorService>()
            .ConfigureConnectorClient("https://www.myfitnesspal.com");
    }
}
