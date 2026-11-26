using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

public static class GoogleHealthPlatformRegistration
{
    /// <remarks>
    ///     Expects <see cref="IGoogleHealthReadingWriter"/> registered: the connector holds it as
    ///     <see cref="ConnectorStoreFailures{TStore}"/> under <see cref="GoogleHealthServiceKeys.Writer"/>,
    ///     and the API's own <see cref="GoogleHealthService"/> keeps the raw writer.
    /// </remarks>
    public static IServiceCollection AddGoogleHealthPlatform(this IServiceCollection services)
    {
        services.AddSingleton<GoogleHealthOAuthState>();
        services.AddScoped<IGoogleHealthPlatformSettings, GoogleHealthPlatformSettings>();
        services.AddKeyedScoped(
            GoogleHealthServiceKeys.Writer,
            (provider, _) => ConnectorStoreFailures<IGoogleHealthReadingWriter>.Over(
                provider.GetRequiredService<IGoogleHealthReadingWriter>()));
        return services;
    }
}
