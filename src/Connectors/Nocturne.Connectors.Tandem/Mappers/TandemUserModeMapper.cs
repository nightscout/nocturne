using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>
/// Maps Tandem user-mode change events (sleep / exercise start and stop) into <see cref="StateSpan"/>
/// records by pairing each start with its matching stop within the processed window. Mirrors
/// <c>tconnectsync</c>'s <c>process_user_mode.py</c>; an unmatched start is left open (no end).
/// </summary>
public sealed class TandemUserModeMapper(ILogger logger, TandemTimeResolver time)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TandemTimeResolver _time = time ?? throw new ArgumentNullException(nameof(time));

    public List<StateSpan> Map(IEnumerable<TandemPumpEvent> events)
    {
        var ordered = events.OrderBy(e => e.RawTimestampSeconds).ToList();
        var spans = new List<StateSpan>();

        TandemPumpEvent? sleepStart = null;
        TandemPumpEvent? exerciseStart = null;

        foreach (var ev in ordered)
        {
            var action = ev.EnumName("RequestedAction");
            switch (action)
            {
                case "Start Sleep":
                    sleepStart = ev;
                    break;
                case "Start Exercise":
                    exerciseStart = ev;
                    break;
                case "Stop Sleep":
                case "Stop All" when sleepStart != null:
                    if (sleepStart != null)
                    {
                        spans.Add(BuildSleep(sleepStart, ev));
                        sleepStart = null;
                    }
                    if (action == "Stop All" && exerciseStart != null)
                    {
                        spans.Add(BuildExercise(exerciseStart, ev));
                        exerciseStart = null;
                    }
                    break;
                case "Stop Exercise":
                    if (exerciseStart != null)
                    {
                        spans.Add(BuildExercise(exerciseStart, ev));
                        exerciseStart = null;
                    }
                    break;
            }
        }

        if (sleepStart != null)
            spans.Add(BuildSleep(sleepStart, null));
        if (exerciseStart != null)
            spans.Add(BuildExercise(exerciseStart, null));

        _logger.LogDebug("Mapped {Count} Tandem user-mode state spans", spans.Count);
        return spans;
    }

    private StateSpan BuildSleep(TandemPumpEvent start, TandemPumpEvent? stop)
    {
        var state = start.EnumName("SleepStartedByGUI") == "TRUE"
            ? "Sleep (Manual)"
            : start.Bits("ActiveSleepSchedule").Count > 0
                ? "Sleep (Scheduled)"
                : "Sleep";
        return Build(StateSpanCategory.Sleep, state, start, stop);
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
