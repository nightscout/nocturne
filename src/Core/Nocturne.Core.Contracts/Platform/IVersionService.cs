using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Platform;

public interface IVersionService
{
    /// <returns>List of supported API versions</returns>
    Task<VersionsResponse> GetSupportedVersionsAsync();

    /// <returns>Version response with system information</returns>
    Task<VersionResponse> GetVersionAsync();
}
