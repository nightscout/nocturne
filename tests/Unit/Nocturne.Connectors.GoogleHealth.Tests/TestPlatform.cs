using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.Connectors.GoogleHealth.Tests;

internal static class TestPlatform
{
    public const string ClientId = "client.apps.googleusercontent.com";

    public static IGoogleHealthPlatformSettings Options() => new FixedGoogleHealthPlatformSettings(
        new GoogleHealthPlatformOptions
        {
            ClientId = ClientId,
            ClientSecret = "client-secret",
            RedirectUri = "https://example.test" + GoogleHealthPlatformOptions.CallbackPath
        });
}
