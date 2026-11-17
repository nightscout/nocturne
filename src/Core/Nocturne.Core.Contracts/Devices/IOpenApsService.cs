using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// The part of the legacy <c>openaps.js</c> plugin other plugins read.
/// </summary>
public interface IOpenApsService
{
    /// <summary>
    /// Port of legacy <c>findOfflineMarker()</c>.
    /// </summary>
    /// <returns>Active offline marker or null if none found</returns>
    Treatment? FindOfflineMarker(IEnumerable<Treatment> treatments, DateTime currentTime);
}
