using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps Tandem user-mode change events (exercise start and stop) into <see cref="StateSpan"/>
/// records by pairing each start with its matching stop within the processed window. Mirrors
/// <c>tconnectsync</c>'s <c>process_user_mode.py</c>; an unmatched start is left open (no end).
/// Control-IQ Sleep-mode events are tracked only to close out an open Exercise span on a
/// "Stop All"; they no longer produce their own span because <c>StateSpanCategory.Sleep</c> was
/// removed (sleep data lives in the sleep_sessions tables, sourced from wearables/health
/// platforms — not from a pump activity mode).
/// </summary>
public sealed class TandemUserModeMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<StateSpan> Map(IEnumerable<TandemPumpEvent> events)
    {
        var ordered = events.OrderBy(e => e.RawTimestampSeconds).ToList();
        var spans = new List<StateSpan>();

        TandemPumpEvent? exerciseStart = null;

        foreach (var ev in ordered)
        {
            var action = ev.EnumName("RequestedAction");
            switch (action)
            {
                case "Start Exercise":
                    exerciseStart = ev;
                    break;
                case "Stop All":
                case "Stop Exercise":
                    if (exerciseStart != null)
                    {
                        spans.Add(BuildExercise(exerciseStart, ev));
                        exerciseStart = null;
                    }
                    break;
            }
        }

        if (exerciseStart != null)
            spans.Add(BuildExercise(exerciseStart, null));

        _logger.LogDebug("Mapped {Count} Tandem user-mode state spans", spans.Count);
        return spans;
    }

    private StateSpan BuildExercise(TandemPumpEvent start, TandemPumpEvent? stop)
    {
        var state = start.EnumName("ExerciseChoice") == "Timed" ? "Exercise (Timed)" : "Exercise";
        if (stop?.EnumName("ExerciseStoppedByTimer") == "True")
            state += " (Stopped by timer)";
        return Build(StateSpanCategory.Exercise, state, start, stop);
    }

    private StateSpan Build(StateSpanCategory category, string state, TandemPumpEvent start, TandemPumpEvent? stop) =>
        new()
        {
            Category = category,
            State = state,
            StartTimestamp = _time.ToUtc(start.RawTimestampSeconds),
            EndTimestamp = stop != null ? _time.ToUtc(stop.RawTimestampSeconds) : null,
            Source = TandemMapHelpers.Source,
            OriginalId = stop != null
                ? $"tandem_usermode_{start.SeqNum}_{stop.SeqNum}"
                : $"tandem_usermode_{start.SeqNum}",
        };
}
