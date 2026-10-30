using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.API.Services.Health.GoogleHealth;

public sealed class GoogleHealthPlatformSettings(
    PlatformSettingsService settings,
    IOptions<BaseDomainOptions> baseDomain) : IGoogleHealthPlatformSettings
{
    public const string Category = "google-health";

    private GoogleHealthPlatformOptions? snapshot;

    // Only a completed read is kept: a cancelled first caller must not fail every later read in the scope.
    public async Task<GoogleHealthPlatformOptions> GetAsync(CancellationToken ct = default) =>
        snapshot ??= await LoadAsync(ct);

    private async Task<GoogleHealthPlatformOptions> LoadAsync(CancellationToken ct)
    {
        var credentials = await settings.GetCredentialsAsync(Category, ct);
        var fields = credentials?.Fields;
        return new GoogleHealthPlatformOptions
        {
            ClientId = credentials?.Enabled == true ? fields?.GetValueOrDefault("clientId") ?? "" : "",
            ClientSecret = credentials?.Enabled == true ? fields?.GetValueOrDefault("clientSecret") ?? "" : "",
            RedirectUri = fields?.GetValueOrDefault("redirectUri") is { Length: > 0 } configured
                ? configured
                : DefaultRedirectUri(baseDomain.Value),
        };
    }

    public static string DefaultRedirectUri(BaseDomainOptions options) =>
        options.PublicOrigin is { } origin ? origin + GoogleHealthPlatformOptions.CallbackPath : "";
}
