using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
/// Maps MyLife glucose events to SensorGlucose records.
/// </summary>
internal sealed class MyLifeSensorGlucoseMapper
{
    internal static IEnumerable<SensorGlucose> Map(IEnumerable<MyLifeEvent> events, bool enableGlucoseSync)
    {
        if (!enableGlucoseSync)
            return [];

        var list = new List<SensorGlucose>();
        foreach (var ev in events)
        {
            if (ev.Deleted)
                continue;

            if (
                ev.EventTypeId
                is not MyLifeEventType.Glucose
                    and not MyLifeEventType.ManualGlucoseAlt
            )
                continue;

            if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var value))
                continue;

            var sensorGlucose = MyLifeFactory.CreateSensorGlucose(ev, value);
            list.Add(sensorGlucose);
        }

        return list;
    }
}
