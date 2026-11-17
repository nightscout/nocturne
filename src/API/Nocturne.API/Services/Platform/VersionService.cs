using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// Provides Nightscout API version information, returning the list of supported API versions
/// (v1, v2, v3) and basic runtime metadata for compatibility with legacy Nightscout clients.
/// </summary>
/// <seealso cref="IVersionService"/>
public class VersionService : IVersionService
{
    private readonly TimeProvider _time;
    private readonly ILogger<VersionService> _logger;

    public VersionService(TimeProvider timeProvider, ILogger<VersionService> logger)
    {
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Get the API levels Nocturne serves, each with its version and mount path.
    /// </summary>
    /// <returns>One entry per supported API level</returns>
    public Task<IReadOnlyList<ApiVersionEntry>> GetSupportedVersionsAsync()
    {
        _logger.LogDebug("Getting supported API versions");

        IReadOnlyList<ApiVersionEntry> versions =
        [
            new(ApplicationConstants.NightscoutApi.V1Version, "/api/v1"),
            new(ApplicationConstants.NightscoutApi.V2Version, "/api/v2"),
            new(ApplicationConstants.NightscoutApi.V3Version, "/api/v3"),
        ];

        return Task.FromResult(versions);
    }

    /// <summary>
    /// Get the current system version information
    /// </summary>
    /// <returns>Version response with system information</returns>
    public async Task<VersionResponse> GetVersionAsync()
    {
        _logger.LogDebug("Getting version information");

        var assemblyName = typeof(VersionService).Assembly.GetName().Name ?? "Nocturne";

        var response = new VersionResponse
        {
            Version = AppVersion.Current,
            Name = assemblyName,
            ServerTime = _time.GetUtcNow().UtcDateTime,
            Head = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown",
            Build = Environment.GetEnvironmentVariable("BUILD_DATE") ?? _time.GetUtcNow().UtcDateTime.ToString("yyyy.MM.dd"),
            ApiCompatibility = "Nightscout v15.0",
        };

        _logger.LogDebug("Returning version {Version} for {Name}", response.Version, response.Name);

        return await Task.FromResult(response);
    }
}
