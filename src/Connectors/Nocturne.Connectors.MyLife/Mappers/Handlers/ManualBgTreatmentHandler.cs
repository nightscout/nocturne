using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class ManualBgTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.ManualGlucose;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        if (!context.EnableManualBgSync) return Enumerable.Empty<Treatment>();

        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var glucose)) return Enumerable.Empty<Treatment>();

        var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);
        var treatment = new Treatment
        {
            Id = $"{MyLifeIdPrefixes.Treatment}{MyLifeMapperHelpers.BuildEventKey(ev)}",
            EventType = MyLifeTreatmentTypes.BgCheck,
            Glucose = glucose,
            GlucoseType = MyLifeGlucoseTypes.Finger,
            Mills = timestamp.ToUnixTimeMilliseconds(),
            Created_at = timestamp.UtcDateTime.ToString(MyLifeFormats.IsoTimestamp),
            EnteredBy = DataSources.MyLifeConnector
        };

        return new[] { treatment };
    }
}