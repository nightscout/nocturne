using Nocturne.Connectors.GoogleHealth.Configurations;

namespace Nocturne.Connectors.GoogleHealth.Tests;

internal sealed class FixedGoogleHealthPlatformSettings(GoogleHealthPlatformOptions options) : IGoogleHealthPlatformSettings
{
    public Task<GoogleHealthPlatformOptions> GetAsync(CancellationToken ct = default) => Task.FromResult(options);
}
