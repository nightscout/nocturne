using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Handlers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
///     Mapper for converting MyLife events to TempBasal records.
///     Produces TempBasal V4 domain model objects for basal delivery tracking.
/// </summary>
internal sealed class MyLifeStateSpanMapper
{
    private static readonly IReadOnlyList<IMyLifeStateSpanHandler> Handlers =
    [
        new BasalRateHandler(),
        new BasalAmountHandler(),
        new TempBasalHandler(),
    ];

    /// <summary>
    ///     Maps MyLife events to TempBasal records for basal delivery.
    /// </summary>
    /// <param name="events">The MyLife events to process</param>
    /// <param name="enableTempBasalConsolidation">Whether to enable temp basal consolidation</param>
    /// <param name="tempBasalConsolidationWindowMinutes">The window for temp basal consolidation</param>
    /// <returns>A collection of TempBasal records</returns>
    internal static IEnumerable<TempBasal> MapTempBasals(
        IEnumerable<MyLifeEvent> events,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes
    )
    {
        var eventList = events.ToList();
        var context = MyLifeContext.Create(
            eventList,
            false,
            enableTempBasalConsolidation,
            tempBasalConsolidationWindowMinutes
        );

        return MapTempBasals(eventList, context);
    }

    /// <summary>
    ///     Maps MyLife events to TempBasal records using a pre-built context. This allows building
    ///     context from a wider event set (e.g. for cross-month consolidation) while only iterating
    ///     a subset for output.
    /// </summary>
    internal static IEnumerable<TempBasal> MapTempBasals(
        IReadOnlyList<MyLifeEvent> events,
        MyLifeContext context
    )
    {
        // When BasalRate (17) events exist, skip Basal (22) events to avoid
        // double-counting. BasalRate events are the authoritative rate source
        // for algorithm pumps (CamAPS); Basal events are redundant delivery
        // confirmations that overlap with the same time periods.
        var hasBasalRateEvents = events.Any(e =>
            !e.Deleted && e.EventTypeId == MyLifeEventType.BasalRate);

        var tempBasals = new List<TempBasal>();
        foreach (var ev in events)
        {
            if (ev.Deleted)
                continue;

            // Skip Basal amount events when BasalRate events are present
            if (hasBasalRateEvents && ev.EventTypeId == MyLifeEventType.Basal)
                continue;

            foreach (var handler in Handlers)
            {
                if (!handler.CanHandleStateSpan(ev))
                    continue;

                tempBasals.AddRange(handler.HandleStateSpan(ev, context));
                break;
            }
        }

        // Post-process to set EndTimestamp on consecutive TempBasal records
        CalculateTempBasalEndTimes(tempBasals);

        // Return sorted by StartTimestamp for consistent ordering
        return tempBasals.OrderBy(t => t.StartTimestamp);
    }

    /// <summary>
    ///     Calculate end times for TempBasal records based on consecutive records.
    ///     When a new basal delivery starts, the previous one ends.
    /// </summary>
    private static void CalculateTempBasalEndTimes(List<TempBasal> tempBasals)
    {
        // Get all TempBasal records without an end time, sorted by start time
        var openRecords = tempBasals
            .Where(t => !t.EndTimestamp.HasValue && t.StartTimestamp > DateTime.MinValue)
            .OrderBy(t => t.StartTimestamp)
            .ToList();

        if (openRecords.Count == 0)
            return;

        // Set each record's end time to the start of the next record
        for (var i = 0; i < openRecords.Count - 1; i++)
        {
            var current = openRecords[i];
            var next = openRecords[i + 1];

            current.EndTimestamp = next.StartTimestamp;
        }

        // The last record remains open (no end time) - it's the current active state
    }
}
