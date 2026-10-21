using Microsoft.Extensions.Options;
using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.Connectors.GoogleHealth.Tests;

internal static class TestPlatform
{
    public const string ClientId = "client.apps.googleusercontent.com";

    public static IOptions<GoogleHealthPlatformOptions> Options() => Microsoft.Extensions.Options.Options.Create(
        new GoogleHealthPlatformOptions
        {
            ClientId = ClientId,
            ClientSecret = "client-secret",
            RedirectUri = "https://example.test" + GoogleHealthPlatformOptions.CallbackPath
        });
}
