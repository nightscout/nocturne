using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

internal static class MyLifeTreatmentFactory
{
    internal static Treatment Create(MyLifeEvent ev, string eventType)
    {
        var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);
        return new Treatment
        {
            Id = $"{MyLifeIdPrefixes.Treatment}{MyLifeMapperHelpers.BuildEventKey(ev)}",
            EventType = eventType,
            Mills = timestamp.ToUnixTimeMilliseconds(),
            Created_at = timestamp.UtcDateTime.ToString(MyLifeFormats.IsoTimestamp)
        };
    }
}
