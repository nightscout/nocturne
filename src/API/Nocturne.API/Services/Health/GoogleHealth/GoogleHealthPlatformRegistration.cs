using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.API.Services.Health.GoogleHealth;

public static class GoogleHealthPlatformRegistration
{
    public static IServiceCollection AddGoogleHealthPlatform(this IServiceCollection services)
    {
        services.AddSingleton<GoogleHealthOAuthState>();
        services.AddScoped<IGoogleHealthPlatformSettings, GoogleHealthPlatformSettings>();
        return services;
    }
}
