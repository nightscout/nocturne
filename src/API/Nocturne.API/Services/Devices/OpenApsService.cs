using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Devices;

/// <inheritdoc cref="IOpenApsService"/>
public class OpenApsService : IOpenApsService
{
    /// <summary>
    /// Finds active OpenAPS offline marker
    /// Implements the legacy findOfflineMarker() functionality with 1:1 compatibility
    /// </summary>
    public Treatment? FindOfflineMarker(IEnumerable<Treatment> treatments, DateTime currentTime)
    {
        if (treatments == null)
            return null;

        return treatments
            .Where(treatment => treatment.EventType == "OpenAPS Offline")
            .Where(treatment =>
            {
                var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).DateTime;
                var eventEnd = treatment.Duration.HasValue
                    ? eventTime.AddMinutes(treatment.Duration.Value)
                    : eventTime;
                return eventTime <= currentTime && eventEnd >= currentTime;
            })
            .OrderByDescending(treatment => treatment.Mills)
            .FirstOrDefault();
    }
}
