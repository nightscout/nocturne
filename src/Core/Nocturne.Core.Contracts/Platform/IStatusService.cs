using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Platform;

public interface IStatusService
{
    Task<StatusResponse> GetSystemStatusAsync();

    /// <summary>
    /// Get the current system status with extended V3 information
    /// </summary>
    /// <returns>V3 status response with extended system information and permissions</returns>
    Task<V3StatusResponse> GetV3SystemStatusAsync();

    /// <returns>Last modified timestamps for each collection</returns>
    Task<LastModifiedResponse> GetLastModifiedAsync();
}
