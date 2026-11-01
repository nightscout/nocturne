using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.API.Tests.Health.GoogleHealth;

internal sealed class FixedGoogleHealthPlatformSettings(GoogleHealthPlatformOptions options) : IGoogleHealthPlatformSettings
{
    public Task<GoogleHealthPlatformOptions> GetAsync(CancellationToken ct = default) => Task.FromResult(options);
}
