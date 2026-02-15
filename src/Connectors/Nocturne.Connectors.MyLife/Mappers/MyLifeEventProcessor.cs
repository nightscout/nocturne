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

    /// <summary>
    ///     Maps MyLife events to StateSpans, primarily BasalDelivery StateSpans.
    ///     These provide pump-confirmed basal delivery tracking with implicit duration model.
    /// </summary>
    /// <param name="events">The MyLife events to process</param>
    /// <param name="enableTempBasalConsolidation">Whether to enable temp basal consolidation</param>
    /// <param name="tempBasalConsolidationWindowMinutes">The window for temp basal consolidation</param>
    /// <returns>A collection of StateSpans</returns>
    public IEnumerable<StateSpan> MapStateSpans(
        IEnumerable<MyLifeEvent> events,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes)
    {
        return MyLifeStateSpanMapper.MapStateSpans(
            events,
            enableTempBasalConsolidation,
            tempBasalConsolidationWindowMinutes);
    }
}