using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Service interface for properties operations with 1:1 legacy JavaScript compatibility
/// Handles sandbox properties and client settings
/// </summary>
public interface IPropertiesService
{
    /// <summary>
    /// Get all properties from the sandbox
    /// </summary>
    /// <returns>All available properties</returns>
    Task<Dictionary<string, object>> GetAllPropertiesAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get specific properties by names
    /// </summary>
    /// <returns>Selected properties</returns>
    Task<Dictionary<string, object>> GetPropertiesAsync(
        IEnumerable<string> propertyNames,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Apply security filtering to remove sensitive properties
    /// </summary>
    /// <param name="properties">Properties to filter</param>
    /// <returns>Filtered properties with sensitive data removed</returns>
    Dictionary<string, object> ApplySecurityFiltering(Dictionary<string, object> properties);
}
