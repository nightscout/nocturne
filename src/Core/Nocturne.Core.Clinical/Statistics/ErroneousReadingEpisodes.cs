using System.Globalization;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Statistics;

/// <summary>
/// Groups what the sensor flagged into episodes for the Data Quality page.
/// </summary>
/// <remarks>
/// An episode of readings needs <see cref="MinInvalidReadings"/> invalid readings. A lone invalid
/// reading sits on the trace like its neighbours, so it is only counted in the summary
/// (<see cref="ErroneousReadingsSummary.IsolatedReadings"/>) and marked on the chart. Uncertain
/// readings join an episode when they fall inside it, and otherwise only count in the summary.
/// </remarks>
public static class ErroneousReadingEpisodes
{
    /// <summary>Flagged readings further apart than this start a new episode.</summary>
    public const double MergeGapMinutes = 15;

    /// <summary>The invalid readings a run needs to be an episode.</summary>
    public const int MinInvalidReadings = 2;

    /// <summary>How far each side of an episode a finger stick still counts as evidence.</summary>
    public const double MeterMinutes = 30;

    /// <summary>A note's episode ends at the next reading, or after this long without one.</summary>
    public const double NoteCapMinutes = 24 * 60;

    /// <summary>Prefix of the notes Trio posts with <c>Upload CGM Sensor States</c> on.</summary>
    public const string TrioNotePrefix = "CGM: ";

    /// <summary>The <see cref="StateSpanCategory.DataExclusion"/> state a reader posts for a sensor error.</summary>
    public const string SensorErrorState = "SensorError";

    private const long MinuteMs = 60_000;

    /// <param name="fromMills">Inclusive start of the window; episodes start inside it.</param>
    /// <param name="toMills">Exclusive end of the window.</param>
    /// <param name="readings">Canonical readings of every quality in the window, in any order.</param>
    /// <param name="spans"><see cref="StateSpanCategory.DataExclusion"/> spans; only <see cref="SensorErrorState"/> ones are used.</param>
    /// <param name="notes">Notes in the window; only Trio's sensor-state notes are used.</param>
    /// <param name="meterReadings">Finger sticks in the window, from meter readings and BG checks.</param>
    /// <param name="zone">The patient's zone, which the days are cut in; null cuts them in UTC.</param>
    public static ErroneousReadingsReport Build(
        long fromMills,
        long toMills,
        IEnumerable<SensorGlucose> readings,
        IEnumerable<StateSpan> spans,
        IEnumerable<Note> notes,
        IEnumerable<ErroneousEpisodeMeterReading> meterReadings,
        TimeZoneInfo? zone = null)
    {
        var sorted = readings.OrderBy(r => r.Mills).ToList();
        var meters = meterReadings.OrderBy(m => m.Mills).ToList();
        var (minutes, _) = GlucoseCadence.ReadingMinutes(sorted);

        var episodes = new List<ErroneousReadingEpisode>();
        var runs = ReadingEpisodes(sorted, minutes).ToList();
        episodes.AddRange(runs.Where(e => e.InvalidCount >= MinInvalidReadings));
        episodes.AddRange(spans
            .Where(s => s.Category == StateSpanCategory.DataExclusion && s.State == SensorErrorState)
            .Select(s => new ErroneousReadingEpisode
            {
                Kind = ErroneousEpisodeKind.Span,
                StartMills = s.StartMills,
                EndMills = s.EndMills,
                Source = s.Source,
                SensorStatuses = s.Metadata?.GetValueOrDefault("sensorStatus") is { } status
                    ? [status.ToString()!]
                    : [],
            }));
        episodes.AddRange(notes
            .Where(IsTrioSensorStateNote)
            .Select(n => NoteEpisode(n, sorted)));

        foreach (var episode in episodes)
        {
            var end = episode.EndMills ?? sorted.LastOrDefault()?.Mills ?? episode.StartMills;
            episode.MeterReadings = meters
                .Where(m => m.Mills >= episode.StartMills - (long)(MeterMinutes * MinuteMs)
                         && m.Mills <= end + (long)(MeterMinutes * MinuteMs))
                .ToList();
        }

        episodes = [.. episodes
            .Where(e => e.StartMills >= fromMills && e.StartMills < toMills)
            .OrderBy(e => e.StartMills)];
        var inWindow = Enumerable.Range(0, sorted.Count)
            .Where(i => sorted[i].Mills >= fromMills && sorted[i].Mills < toMills)
            .ToList();
        // With a minimum of two, a run below it holds one invalid reading, and ToEpisode starts the
        // run at that reading.
        List<long> isolated = [.. runs
            .Where(e => e.InvalidCount < MinInvalidReadings
                     && e.StartMills >= fromMills && e.StartMills < toMills)
            .Select(e => e.StartMills)];
        return new ErroneousReadingsReport
        {
            Days = ByDay(episodes, isolated, zone ?? TimeZoneInfo.Utc),
            Summary = new ErroneousReadingsSummary
            {
                Episodes = episodes.Count,
                InvalidReadings = inWindow.Count(i => sorted[i].Quality == SensorGlucoseQuality.Invalid),
                IsolatedReadings = isolated.Count,
                UncertainReadings = inWindow.Count(i => sorted[i].Quality == SensorGlucoseQuality.Uncertain),
                ExcludedMinutes = Math.Round(inWindow
                    .Where(i => sorted[i].Quality == SensorGlucoseQuality.Invalid)
                    .Sum(i => minutes[i]), 1),
            },
        };
    }

    /// <summary>The page shows one card and one chart per local day, so the API cuts the days.</summary>
    private static List<ErroneousReadingsDay> ByDay(
        List<ErroneousReadingEpisode> episodes, List<long> isolated, TimeZoneInfo zone)
    {
        var days = new SortedDictionary<DateOnly, ErroneousReadingsDay>();
        ErroneousReadingsDay DayOf(long mills)
        {
            var date = LocalSlotBinning.LocalDay(mills, zone);
            if (!days.TryGetValue(date, out var day))
            {
                day = new ErroneousReadingsDay
                {
                    Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    FromMills = LocalSlotBinning.DayStartMills(date, zone),
                    ToMills = LocalSlotBinning.DayStartMills(date.AddDays(1), zone),
                };
                days[date] = day;
            }
            return day;
        }

        foreach (var episode in episodes)
        {
            var day = DayOf(episode.StartMills);
            day.Episodes.Add(episode);
            day.InvalidReadings += episode.InvalidCount;
            if (episode.EndMills > day.ToMills)
                day.ToMills = episode.EndMills.Value;
        }
        foreach (var mills in isolated)
        {
            var day = DayOf(mills);
            day.IsolatedReadingMills.Add(mills);
            day.InvalidReadings++;
        }
        return [.. days.Values.Reverse()];
    }

    public static bool IsTrioSensorStateNote(Note note) =>
        note.Text.StartsWith(TrioNotePrefix, StringComparison.Ordinal)
        && string.Equals(note.Device, "Trio", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ErroneousReadingEpisode> ReadingEpisodes(List<SensorGlucose> sorted, double[] minutes)
    {
        var flagged = Enumerable.Range(0, sorted.Count)
            .Where(i => sorted[i].Quality is SensorGlucoseQuality.Invalid or SensorGlucoseQuality.Uncertain)
            .ToList();

        var run = new List<int>();
        foreach (var i in flagged)
        {
            if (run.Count > 0 && sorted[i].Mills - sorted[run[^1]].Mills > MergeGapMinutes * MinuteMs)
            {
                if (ToEpisode(run, sorted, minutes) is { } episode) yield return episode;
                run = [];
            }
            run.Add(i);
        }
        if (ToEpisode(run, sorted, minutes) is { } last) yield return last;
    }

    private static ErroneousReadingEpisode? ToEpisode(List<int> run, List<SensorGlucose> sorted, double[] minutes)
    {
        var invalid = run.Where(i => sorted[i].Quality == SensorGlucoseQuality.Invalid).ToList();
        if (invalid.Count == 0)
            return null;

        // Uncertain readings only frame the invalid ones; leading or trailing ones stay outside.
        var members = run.Where(i => i >= invalid[0] && i <= invalid[^1]).ToList();
        return new ErroneousReadingEpisode
        {
            Kind = ErroneousEpisodeKind.Readings,
            StartMills = sorted[members[0]].Mills,
            EndMills = sorted[members[^1]].Mills,
            Source = sorted[members[0]].DataSource ?? sorted[members[0]].Device,
            InvalidCount = invalid.Count,
            UncertainCount = members.Count - invalid.Count,
            ExcludedMinutes = Math.Round(invalid.Sum(i => minutes[i]), 1),
            MinMgdl = members.Min(i => sorted[i].Mgdl),
            MaxMgdl = members.Max(i => sorted[i].Mgdl),
            SensorStatuses = [.. members.Select(i => sorted[i].SensorStatus).OfType<string>().Distinct()],
        };
    }

    private static ErroneousReadingEpisode NoteEpisode(Note note, List<SensorGlucose> sorted)
    {
        var next = sorted.FirstOrDefault(r => r.Mills > note.Mills);
        var cap = note.Mills + (long)(NoteCapMinutes * MinuteMs);
        return new ErroneousReadingEpisode
        {
            Kind = ErroneousEpisodeKind.Note,
            StartMills = note.Mills,
            EndMills = next is null ? null : Math.Min(next.Mills, cap),
            Source = note.Device,
            SensorStatuses = [note.Text[TrioNotePrefix.Length..].Trim()],
        };
    }
}
