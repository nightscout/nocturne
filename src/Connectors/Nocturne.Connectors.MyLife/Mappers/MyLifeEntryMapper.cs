using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

internal sealed class MyLifeEntryMapper
{
    internal IEnumerable<Entry> MapEntries(IEnumerable<MyLifeEvent> events, bool enableGlucoseSync)
    {
        if (!enableGlucoseSync) return [];

        var list = new List<Entry>();
        foreach (var ev in events)
        {
            if (ev.Deleted) continue;

            if (ev.EventTypeId != MyLifeEventTypeIds.Glucose &&
                ev.EventTypeId != MyLifeEventTypeIds.ManualGlucoseAlt)
                continue;

            if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var value)) continue;

            var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);
            var entry = new Entry
            {
                Id = $"{MyLifeIdPrefixes.Entry}{MyLifeMapperHelpers.BuildEventKey(ev)}",
                Type = MyLifeEntryTypes.Sgv,
                Sgv = value,
                Mills = timestamp.ToUnixTimeMilliseconds(),
                DateString = timestamp.UtcDateTime.ToString(MyLifeFormats.IsoTimestamp),
                Device = DataSources.MyLifeConnector
            };
            list.Add(entry);
        }

        return list;
    }
}