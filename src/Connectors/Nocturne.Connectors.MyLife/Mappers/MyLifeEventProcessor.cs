using Nocturne.Connectors.MyLife.Mappers.Mappers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

public class MyLifeEventProcessor
{
    private readonly MyLifeEntryMapper _entryMapper = new();

    public IEnumerable<Entry> MapEntries(IEnumerable<MyLifeEvent> events, bool enableGlucoseSync)
    {
        return _entryMapper.MapEntries(events, enableGlucoseSync);
    }

    public IEnumerable<Treatment> MapTreatments(
        IEnumerable<MyLifeEvent> events,
        bool enableManualBgSync,
        bool enableMealCarbConsolidation,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes)
    {
        return MyLifeTreatmentMapper.MapTreatments(
            events,
            enableManualBgSync,
            enableMealCarbConsolidation,
            enableTempBasalConsolidation,
            tempBasalConsolidationWindowMinutes);
    }
}
