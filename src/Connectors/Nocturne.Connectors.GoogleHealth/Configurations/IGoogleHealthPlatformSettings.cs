namespace Nocturne.Connectors.GoogleHealth.Configurations;

public interface IGoogleHealthPlatformSettings
{
    Task<GoogleHealthPlatformOptions> GetAsync(CancellationToken ct = default);
}
