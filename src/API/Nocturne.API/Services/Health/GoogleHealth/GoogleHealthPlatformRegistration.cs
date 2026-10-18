using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.API.Services.Health.GoogleHealth;

public static class GoogleHealthPlatformRegistration
{
    public static IServiceCollection AddGoogleHealthPlatform(this IServiceCollection services)
    {
        services.AddSingleton<GoogleHealthOAuthState>();
        services.AddOptions<GoogleHealthPlatformOptions>()
            .PostConfigure<IOptions<BaseDomainOptions>>((platform, baseDomain) =>
            {
                if (string.IsNullOrEmpty(platform.RedirectUri) && baseDomain.Value.PublicOrigin is { } origin)
                    platform.RedirectUri = origin + GoogleHealthPlatformOptions.CallbackPath;
            });
        return services;
    }
}
