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
        var enabled = credentials?.Enabled == true;
        return new GoogleHealthPlatformOptions
        {
            ClientId = enabled ? Field(credentials, "clientId") ?? "" : "",
            ClientSecret = enabled ? Field(credentials, "clientSecret") ?? "" : "",
            RedirectUri = Field(credentials, "redirectUri") ?? DefaultRedirectUri(baseDomain.Value),
        };
    }

    /// <summary>A stored value without surrounding whitespace; null when unset or blank.</summary>
    public static string? Field(PlatformCredentials? credentials, string name) =>
        credentials?.Fields.GetValueOrDefault(name)?.Trim() is { Length: > 0 } value ? value : null;

    public static string DefaultRedirectUri(BaseDomainOptions options) =>
        options.PublicOrigin is { } origin ? origin + GoogleHealthPlatformOptions.CallbackPath : "";
}
