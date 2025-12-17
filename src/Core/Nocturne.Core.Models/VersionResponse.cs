namespace Nocturne.Core.Models;

/// <summary>
/// Version response model that maintains 1:1 compatibility with Nightscout version endpoints
/// </summary>
public class VersionResponse
{
    /// <summary>
    /// Server version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Server name/title
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Current server time in ISO 8601 format
    /// </summary>
    public DateTime ServerTime { get; set; }

    /// <summary>
    /// Head of repository
    /// </summary>
    public string Head { get; set; } = string.Empty;

    /// <summary>
    /// Build identifier or date
    /// </summary>
    public string Build { get; set; } = string.Empty;

    /// <summary>
    /// API Compatibility version (e.g. Nightscout v15)
    /// </summary>
    public string ApiCompatibility { get; set; } = string.Empty;
}
