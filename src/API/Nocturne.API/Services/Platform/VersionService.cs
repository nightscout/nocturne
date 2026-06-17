using System.Reflection;
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
    private readonly ILogger<VersionService> _logger;

    public VersionService(ILogger<VersionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get the list of supported API versions
    /// </summary>
    /// <returns>List of supported API versions</returns>
    public async Task<VersionsResponse> GetSupportedVersionsAsync()
    {
        _logger.LogDebug("Getting supported API versions");

        var response = new VersionsResponse
        {
            Versions = new List<string> { "1", "2", "3" },
        };

        _logger.LogDebug("Returning {VersionCount} supported versions", response.Versions.Count);

        return await Task.FromResult(response);
    }

    /// <summary>
    /// Get the current system version information
    /// </summary>
    /// <returns>Version response with system information</returns>
    public async Task<VersionResponse> GetVersionAsync()
    {
        _logger.LogDebug("Getting version information");

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
        var assemblyName = assembly.GetName().Name ?? "Nocturne";

        var response = new VersionResponse
        {
            Version = version,
            Name = assemblyName,
            ServerTime = DateTime.UtcNow,
            Head = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown",
            Build = Environment.GetEnvironmentVariable("BUILD_DATE") ?? DateTime.UtcNow.ToString("yyyy.MM.dd"),
            ApiCompatibility = "Nightscout v15.0",
        };

        _logger.LogDebug("Returning version {Version} for {Name}", response.Version, response.Name);

        return await Task.FromResult(response);
    }
}
