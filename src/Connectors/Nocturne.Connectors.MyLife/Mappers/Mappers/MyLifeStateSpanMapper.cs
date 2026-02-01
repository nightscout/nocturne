using Nocturne.Connectors.MyLife.Mappers.Handlers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Mappers;

/// <summary>
/// Mapper for converting MyLife events to StateSpans.
/// Focuses on BasalDelivery StateSpans for pump-confirmed basal delivery tracking.
/// </summary>
internal sealed class MyLifeStateSpanMapper
{
    private static readonly IReadOnlyList<IMyLifeStateSpanHandler> Handlers =
    [
        new BasalRateTreatmentHandler(),
        new BasalAmountTreatmentHandler(),
        new TempBasalTreatmentHandler(),
    ];

    /// <summary>
    /// Maps MyLife events to StateSpans, primarily BasalDelivery StateSpans.
    /// </summary>
    /// <param name="events">The MyLife events to process</param>
    /// <param name="enableTempBasalConsolidation">Whether to enable temp basal consolidation</param>
    /// <param name="tempBasalConsolidationWindowMinutes">The window for temp basal consolidation</param>
    /// <returns>A collection of StateSpans</returns>
    internal static IEnumerable<StateSpan> MapStateSpans(
        IEnumerable<MyLifeEvent> events,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes)
    {
        // Create a context - we reuse the treatment context for consistency
        // with the temp basal consolidation logic
        var context = MyLifeTreatmentContext.Create(
            events,
            enableManualBgSync: false,
            enableMealCarbConsolidation: false,
            enableTempBasalConsolidation,
            tempBasalConsolidationWindowMinutes);

        var stateSpans = new List<StateSpan>();
        foreach (var ev in events)
        {
            if (ev.Deleted)
            {
                continue;
            }

            foreach (var handler in Handlers)
            {
                if (!handler.CanHandleStateSpan(ev))
                {
                    continue;
                }

                stateSpans.AddRange(handler.HandleStateSpan(ev, context));
                break;
            }
        }

        // Post-process to set endMills on consecutive BasalDelivery spans
        CalculateBasalDeliveryEndTimes(stateSpans);

        // Return sorted by StartMills for consistent ordering
        return stateSpans.OrderBy(s => s.StartMills);
    }

    /// <summary>
    /// Calculate end times for BasalDelivery StateSpans based on consecutive records.
    /// When a new basal delivery starts, the previous one ends.
    /// </summary>
    private static void CalculateBasalDeliveryEndTimes(List<StateSpan> stateSpans)
    {
        // Get all BasalDelivery spans without an end time, sorted by start time
        var basalSpans = stateSpans
            .Where(s =>
                s.Category == StateSpanCategory.BasalDelivery
                && !s.EndMills.HasValue
                && s.StartMills > 0)
            .OrderBy(s => s.StartMills)
            .ToList();

        if (basalSpans.Count == 0)
        {
            return;
        }

        // Set each span's end time to the start of the next span
        for (int i = 0; i < basalSpans.Count - 1; i++)
        {
            var current = basalSpans[i];
            var next = basalSpans[i + 1];

            // Set the end time to the start of the next span
            current.EndMills = next.StartMills;

            // Calculate the duration and add it to metadata
            var durationMs = next.StartMills - current.StartMills;
            var durationMinutes = durationMs / 60000.0;

            // Add computed duration to metadata
            current.Metadata ??= new Dictionary<string, object>();
            current.Metadata["durationMinutes"] = durationMinutes;

            // If we have a rate, calculate insulin delivered
            if (current.Metadata.TryGetValue("rate", out var rateObj) && rateObj is double rate)
            {
                // Rate is U/h, duration is in minutes
                var insulinDelivered = (rate * durationMinutes) / 60.0;
                current.Metadata["insulinDelivered"] = Math.Round(insulinDelivered, 4);
            }
        }

        // The last span remains open (no end time) - it's the current active state
    }
}
