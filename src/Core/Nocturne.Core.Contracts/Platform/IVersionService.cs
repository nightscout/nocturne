using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Platform;

public interface IVersionService
{
    /// <summary>
    /// Get the list of supported API versions
    /// </summary>
    /// <returns>List of supported API versions</returns>
    Task<VersionsResponse> GetSupportedVersionsAsync();

    /// <summary>
    /// Get the current system version information
    /// </summary>
    /// <returns>Version response with system information</returns>
    Task<VersionResponse> GetVersionAsync();
}
