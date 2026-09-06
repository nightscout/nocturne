using System.Globalization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Sleep.Report;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Sleep;

/// <summary>
/// Pure static computation helpers for sleep report statistics.
/// No dependencies — all inputs are passed as parameters.
/// </summary>
internal static class SleepReportCalculator
{
    private static readonly TimeSpan GlucoseStalenessLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DawnWindowSize        = TimeSpan.FromHours(2);
    private static readonly TimeSpan PostSleepWindow       = TimeSpan.FromMinutes(5);
    private const int DawnMinReadings = 4;

    private static readonly SleepSource[] SourcePriority =
    [
        SleepSource.Oura, SleepSource.Garmin, SleepSource.Apple,
        SleepSource.Samsung, SleepSource.Fitbit, SleepSource.Manual, SleepSource.Google,
    ];

    // ── Stage Breakdown ────────────────────────────────────────────────────

    internal static SleepStageBreakdown ComputeStageBreakdown(SleepSession session)
    {
        int deep, rem, light, awake, unspecified;

        if (session.DeepSleepMs.HasValue && session.RemSleepMs.HasValue
            && session.LightSleepMs.HasValue && session.TotalAwakeMs.HasValue)
        {
            deep  = (int)(session.DeepSleepMs.Value  / 60_000);
            rem   = (int)(session.RemSleepMs.Value   / 60_000);
            light = (int)(session.LightSleepMs.Value / 60_000);
            awake = (int)(session.TotalAwakeMs.Value / 60_000);

            // Some devices report a TotalSleepMs larger than the sum of the differentiated
            // stages; the remainder is asleep time the device didn't stage-classify.
            var totalSleep = (int)(session.TotalSleepMs / 60_000);
            unspecified = Math.Max(0, totalSleep - (deep + rem + light));
        }
        else
        {
            deep = rem = light = awake = unspecified = 0;
            foreach (var stage in session.Stages ?? [])
            {
                var mins = (int)(stage.EndTime - stage.StartTime).TotalMinutes;
                switch (stage.Stage)
                {
                    case SleepStageType.Deep:                                     deep        += mins; break;
                    case SleepStageType.Rem:                                      rem         += mins; break;
                    case SleepStageType.Light:                                    light       += mins; break;
                    case SleepStageType.Asleep:                                   unspecified += mins; break;
                    case SleepStageType.Awake: case SleepStageType.AwakeInBed:
                    case SleepStageType.Restless:                                 awake       += mins; break;
                }
            }
        }

        var total = deep + rem + light + awake + unspecified;
        return new SleepStageBreakdown
        {
            DeepMinutes        = deep,
            RemMinutes         = rem,
            LightMinutes       = light,
            AwakeMinutes       = awake,
            UnspecifiedMinutes = unspecified,
            TotalMinutes       = total,
            DeepPct        = total > 0 ? deep        * 100.0 / total : 0,
            RemPct         = total > 0 ? rem         * 100.0 / total : 0,
            LightPct       = total > 0 ? light       * 100.0 / total : 0,
            AwakePct       = total > 0 ? awake       * 100.0 / total : 0,
            UnspecifiedPct = total > 0 ? unspecified * 100.0 / total : 0,
        };
    }

    // ── Overnight TIR ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes overnight time-in-range percentages. Bucketing matches
    /// <c>StatisticsService.CalculateTimeInRange</c>: very low is <c>&lt; VeryLow</c>,
    /// low is <c>&lt; Low</c>, very high is <c>&gt; VeryHigh</c>, high is
    /// <c>&gt; TargetTop</c>, and everything else is in range.
    /// </summary>
    internal static SleepOvernightTir? ComputeOvernightTir(
        SleepSession session, IEnumerable<SensorGlucose> allGlucose, GlycemicThresholds thresholds)
    {
        var asleepAt = session.SleepLatencyMs.HasValue
            ? session.StartTime.AddMilliseconds(session.SleepLatencyMs.Value)
            : session.StartTime;
        var readings = allGlucose
            .Where(g => g.Timestamp >= asleepAt && g.Timestamp <= session.EndTime)
            .ToList();

        if (readings.Count == 0) return null;

        int veryLow = 0, low = 0, inRange = 0, high = 0, veryHigh = 0;
        double sum = 0;

        foreach (var g in readings)
        {
            sum += g.Mgdl;
            if      (g.Mgdl < thresholds.VeryLow)   veryLow++;
            else if (g.Mgdl < thresholds.Low)       low++;
            else if (g.Mgdl > thresholds.VeryHigh)  veryHigh++;
            else if (g.Mgdl > thresholds.TargetTop) high++;
            else                                    inRange++;
        }

        var n = (double)readings.Count;
        return new SleepOvernightTir
        {
            VeryLowPct  = veryLow  / n * 100,
            LowPct      = low      / n * 100,
            InRangePct  = inRange  / n * 100,
            HighPct     = high     / n * 100,
            VeryHighPct = veryHigh / n * 100,
            MeanBg      = (int)Math.Round(sum / n),
        };
    }

    // ── Hypo Events ───────────────────────────────────────────────────────

    internal static IReadOnlyList<SleepHypoEvent> ComputeHypoEvents(
        SleepSession session,
        IEnumerable<SensorGlucose> allGlucose,
        IEnumerable<SleepStageInterval> stages,
        GlycemicThresholds thresholds)
    {
        var asleepAt = session.SleepLatencyMs.HasValue
            ? session.StartTime.AddMilliseconds(session.SleepLatencyMs.Value)
            : session.StartTime;
        var readings = allGlucose
            .Where(g => g.Timestamp >= asleepAt && g.Timestamp <= session.EndTime)
            .OrderBy(g => g.Timestamp)
            .ToList();

        var stageList = stages.ToList();
        var events    = new List<SleepHypoEvent>();
        SensorGlucose? runStart = null;
        SensorGlucose? nadir    = null;
        SensorGlucose? prev     = null;

        foreach (var g in readings)
        {
            if (g.Mgdl < thresholds.Low)
            {
                runStart ??= g;
                if (nadir == null || g.Mgdl < nadir.Mgdl) nadir = g;
            }
            else if (runStart != null && nadir != null && prev != null)
            {
                events.Add(BuildHypoEvent(runStart, prev, nadir, stageList, thresholds));
                runStart = nadir = null;
            }
            prev = g;
        }

        if (runStart != null && nadir != null && prev != null)
            events.Add(BuildHypoEvent(runStart, prev, nadir, stageList, thresholds));

        return events;
    }

    private static SleepHypoEvent BuildHypoEvent(
        SensorGlucose start, SensorGlucose end, SensorGlucose nadir,
        IEnumerable<SleepStageInterval> stages, GlycemicThresholds thresholds)
    {
        var stage = stages.FirstOrDefault(s =>
            s.StartTime <= nadir.Timestamp && s.EndTime >= nadir.Timestamp)?.Stage
            ?? SleepStageType.Unknown;

        return new SleepHypoEvent
        {
            StartAt         = start.Timestamp,
            EndAt           = end.Timestamp,
            DurationMinutes = (int)(end.Timestamp - start.Timestamp).TotalMinutes,
            LowestBg        = (int)Math.Round(nadir.Mgdl),
            Stage           = stage,
            // Strict < matches StatisticsService.CalculateEpisodes' VeryLow classification.
            Severity        = nadir.Mgdl < thresholds.VeryLow
                                ? SleepHypoSeverity.VeryLow
                                : SleepHypoSeverity.Low,
        };
    }

    // ── Dawn Phenomenon ───────────────────────────────────────────────────

    internal static SleepDawnPhenomenon? ComputeDawnPhenomenon(
        SleepSession session, IEnumerable<SensorGlucose> allGlucose)
    {
        var windowStart = session.EndTime - DawnWindowSize;
        var readings = allGlucose
            .Where(g => g.Timestamp >= windowStart && g.Timestamp <= session.EndTime)
            .OrderBy(g => g.Timestamp)
            .ToList();

        if (readings.Count < DawnMinReadings) return null;

        var troughReading = readings.MinBy(g => g.Mgdl)!;
        var peakReading   = readings.MaxBy(g => g.Mgdl)!;

        var trough = (int)Math.Round(troughReading.Mgdl);
        var peak   = (int)Math.Round(peakReading.Mgdl);

        // Use the net change from first to last reading for direction (positive = rising into wake).
        var signedDelta  = (int)Math.Round(readings.Last().Mgdl - readings.First().Mgdl);
        var windowHours  = (readings.Last().Timestamp - readings.First().Timestamp).TotalHours;
        var rate         = windowHours > 0 ? signedDelta / windowHours : 0.0;

        return new SleepDawnPhenomenon
        {
            WindowStart        = windowStart,
            WindowEnd          = session.EndTime,
            TroughBg           = trough,
            PeakBg             = peak,
            DeltaBg            = signedDelta,
            RateOfClimbPerHour = rate,
        };
    }

    // ── Wake Events ───────────────────────────────────────────────────────

    internal static IReadOnlyList<SleepWakeEvent> ComputeWakeEvents(
        SleepSession session,
        IEnumerable<SleepStageInterval> stages,
        IEnumerable<SensorGlucose> allGlucose)
    {
        var awakeIntervals = stages
            .Where(s => s.Stage is SleepStageType.Awake or SleepStageType.AwakeInBed)
            .OrderBy(s => s.StartTime)
            .ToList();

        var glucose = allGlucose
            .Where(g => g.Timestamp >= session.StartTime && g.Timestamp <= session.EndTime)
            .OrderBy(g => g.Timestamp)
            .ToList();

        var sleepOnset = session.SleepLatencyMs.HasValue
            ? session.StartTime.AddMilliseconds(session.SleepLatencyMs.Value)
            : stages.Where(s => s.Stage is not SleepStageType.Awake and not SleepStageType.AwakeInBed)
                    .MinBy(s => s.StartTime)?.StartTime ?? session.StartTime;

        return awakeIntervals.Select(interval =>
        {
            var nearest = glucose.MinBy(g => Math.Abs((g.Timestamp - interval.StartTime).TotalSeconds));

            var bg = nearest != null
                && Math.Abs((nearest.Timestamp - interval.StartTime).TotalMinutes) <= GlucoseStalenessLimit.TotalMinutes
                ? (int?)Math.Round(nearest.Mgdl) : null;

            return new SleepWakeEvent
            {
                StartAt         = interval.StartTime,
                EndAt           = interval.EndTime,
                DurationMinutes = (int)(interval.EndTime - interval.StartTime).TotalMinutes,
                BgAtStart       = bg,
                IsPreSleep      = interval.EndTime <= sleepOnset,
                IsPostSleep     = interval.StartTime >= session.EndTime - PostSleepWindow,
            };
        }).ToList();
    }

    // ── Score Resolution ──────────────────────────────────────────────────

    internal static (int? Score, SleepScoreSource? Source) ResolveScore(
        SleepSession session, int hypoCount, SleepStageBreakdown breakdown)
    {
        if (session.SleepScore.HasValue)
            return (session.SleepScore.Value, SleepScoreSource.Device);

        // Undifferentiated-only data (e.g. manual entries) carries no stage composition
        // to score against — fabricating a number from it would be misleading.
        var differentiated = breakdown.DeepMinutes + breakdown.RemMinutes + breakdown.LightMinutes;
        if (differentiated == 0) return (null, null);

        var total = (double)breakdown.TotalMinutes;
        var efficiency = (breakdown.DeepMinutes + breakdown.RemMinutes + breakdown.LightMinutes + breakdown.UnspecifiedMinutes) / total;
        var deepFrac   = breakdown.DeepMinutes  / total;
        var remFrac    = breakdown.RemMinutes   / total;
        var disruption = Math.Min(20, breakdown.AwakeMinutes * 0.6 + hypoCount * 4);
        var raw        = 40 + efficiency * 25 + deepFrac * 90 + remFrac * 35 - disruption;
        var score      = (int)Math.Round(Math.Clamp(raw, 0, 100));

        return ((int?)score, (SleepScoreSource?)SleepScoreSource.Computed);
    }

    // ── Night Summary ─────────────────────────────────────────────────────

    internal static SleepNightSummary ComputeNightSummary(
        SleepSession session, IEnumerable<SensorGlucose> sessionGlucose, GlycemicThresholds thresholds)
    {
        var glucose   = sessionGlucose.ToList();
        var breakdown = ComputeStageBreakdown(session);
        var hypos     = ComputeHypoEvents(session, glucose, session.Stages ?? [], thresholds);
        var tir       = ComputeOvernightTir(session, glucose, thresholds);
        var dawn      = ComputeDawnPhenomenon(session, glucose);
        var (finalScore, scoreSource) = ResolveScore(session, hypos.Count, breakdown);

        var sessionReadings = glucose
            .Where(g => g.Timestamp >= session.StartTime && g.Timestamp <= session.EndTime)
            .ToList();

        _ = Guid.TryParse(session.Id, out var sessionId);

        return new SleepNightSummary
        {
            SessionId          = sessionId,
            Date               = session.StartTime.ToString("MMM d"),
            DisplayDate        = NightDate(session).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Weekday            = session.StartTime.DayOfWeek.ToString()[..3],
            InBedAt            = session.StartTime,
            WakeAt             = session.EndTime,
            SleepMinutes       = breakdown.DeepMinutes + breakdown.RemMinutes + breakdown.LightMinutes + breakdown.UnspecifiedMinutes,
            DeepMinutes        = breakdown.DeepMinutes,
            RemMinutes         = breakdown.RemMinutes,
            LightMinutes       = breakdown.LightMinutes,
            AwakeMinutes       = breakdown.AwakeMinutes,
            UnspecifiedMinutes = breakdown.UnspecifiedMinutes,
            SleepScore         = finalScore,
            ScoreSource        = scoreSource,
            OvernightTirPct    = tir?.InRangePct,
            HypoCount          = hypos.Count,
            LowestBg           = sessionReadings.Count > 0
                                  ? (int)Math.Round(sessionReadings.Min(g => g.Mgdl))
                                  : null,
            DawnRiseDeltaMg    = dawn?.DeltaBg,
            HrvMeanMs          = session.AvgHrv,
        };
    }

    // ── Deduplication ─────────────────────────────────────────────────────

    /// <summary>
    /// Collapses concurrent multi-device recordings to one session per night.
    /// Sessions are bucketed by a noon-to-noon night key: the UTC start time is
    /// converted to the session's IANA <see cref="SleepSession.Timezone"/> (falling
    /// back to UTC when the timezone is null or unresolvable), then shifted back
    /// 12 hours and truncated to a date — so any start before local noon belongs
    /// to the previous calendar day's night. Within a bucket the session with the
    /// most sleep wins, tie-broken by source priority.
    /// </summary>
    internal static IReadOnlyList<SleepSession> DeduplicateToOnePerNight(
        IEnumerable<SleepSession> sessions)
    {
        return sessions
            .GroupBy(NightKey)
            .Select(g => g
                .OrderByDescending(s => s.TotalSleepMs)
                .ThenBy(s =>
                {
                    var idx = Array.IndexOf(SourcePriority, s.Source);
                    return idx == -1 ? int.MaxValue : idx;
                })
                .First())
            .OrderBy(s => s.StartTime)
            .ToList();
    }

    /// <summary>
    /// The display-night date a session buckets to under the noon rule (in the
    /// session's timezone). The single-night by-date lookup matches on this so a
    /// deep-linked date resolves to the same night the trends views show.
    /// </summary>
    internal static DateOnly NightDate(SleepSession session) => DateOnly.FromDateTime(NightKey(session));

    private static DateTime NightKey(SleepSession session)
    {
        var localStart = session.StartTime;

        // Resolve through the shared helper rather than FindSystemTimeZoneById: it
        // recovers the mis-cased ids connectors emit, and it does not throw on the
        // ones it cannot resolve — an unresolvable id keys on UTC.
        if (TimeZoneHelper.TryGetTimeZoneInfoFromId(session.Timezone, out var tz))
        {
            localStart = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(session.StartTime, DateTimeKind.Utc), tz);
        }

        return localStart.AddHours(-12).Date;
    }

    // ── Trends Summary ────────────────────────────────────────────────────

    internal static SleepTrendsSummary ComputeTrendsSummary(
        IReadOnlyList<SleepNightSummary> nights,
        int daysInRange,
        SleepStageReferenceRangeSet? referenceRanges = null)
    {
        referenceRanges ??= SleepStageReferenceRangeSet.Default;
        var coveragePct = daysInRange > 0 ? Math.Min(100.0, nights.Count * 100.0 / daysInRange) : 0;

        if (nights.Count == 0)
            return new SleepTrendsSummary
            {
                DaysInRange = daysInRange,
                CoveragePct = coveragePct,
                ReferenceRanges = referenceRanges,
            };

        var scored     = nights.Where(n => n.SleepScore.HasValue).ToList();
        var tirNights  = nights.Where(n => n.OvernightTirPct.HasValue).ToList();
        var totalSleep = nights.Sum(n => n.SleepMinutes);
        var totalDeep  = nights.Sum(n => n.DeepMinutes);
        var totalRem   = nights.Sum(n => n.RemMinutes);

        var last7  = nights.TakeLast(7).ToList();
        // Exclude the last-7 window so the two never overlap; empty when fewer
        // than 8 nights exist, which nulls every delta below.
        var prior7 = nights.SkipLast(7).TakeLast(7).ToList();

        static double? MeanScore(IList<SleepNightSummary> ns) =>
            ns.Any(n => n.SleepScore.HasValue)
                ? ns.Where(n => n.SleepScore.HasValue).Average(n => (double)n.SleepScore!.Value)
                : null;

        static double? MeanTir(IList<SleepNightSummary> ns) =>
            ns.Any(n => n.OvernightTirPct.HasValue)
                ? ns.Where(n => n.OvernightTirPct.HasValue).Average(n => n.OvernightTirPct!.Value)
                : null;

        var l7Score = MeanScore(last7);
        var p7Score = MeanScore(prior7);
        var l7Tir   = MeanTir(last7);
        var p7Tir   = MeanTir(prior7);
        var l7Deep  = last7.Any()  ? last7.Average(n => n.DeepMinutes)  : (double?)null;
        var p7Deep  = prior7.Any() ? prior7.Average(n => n.DeepMinutes) : (double?)null;
        var l7Dawn  = last7.Any(n => n.DawnRiseDeltaMg.HasValue)
                          ? last7.Where(n => n.DawnRiseDeltaMg.HasValue).Average(n => (double)n.DawnRiseDeltaMg!.Value)
                          : (double?)null;
        var p7Dawn  = prior7.Any(n => n.DawnRiseDeltaMg.HasValue)
                          ? prior7.Where(n => n.DawnRiseDeltaMg.HasValue).Average(n => (double)n.DawnRiseDeltaMg!.Value)
                          : (double?)null;

        return new SleepTrendsSummary
        {
            NightCount        = nights.Count,
            DaysInRange       = daysInRange,
            CoveragePct       = coveragePct,
            MeanScore         = scored.Count   > 0 ? scored.Average(n => (double)n.SleepScore!.Value) : null,
            MeanTirPct        = tirNights.Count > 0 ? tirNights.Average(n => n.OvernightTirPct!.Value) : null,
            MeanAsleepMinutes = nights.Average(n => n.SleepMinutes),
            MeanDeepPct       = totalSleep > 0 ? totalDeep * 100.0 / totalSleep : 0,
            MeanRemPct        = totalSleep > 0 ? totalRem  * 100.0 / totalSleep : 0,
            MeanDawnRiseMg    = nights.Any(n => n.DawnRiseDeltaMg.HasValue)
                                  ? nights.Where(n => n.DawnRiseDeltaMg.HasValue).Average(n => (double)n.DawnRiseDeltaMg!.Value)
                                  : null,
            MeanHrvMs         = nights.Any(n => n.HrvMeanMs.HasValue)
                                  ? nights.Where(n => n.HrvMeanMs.HasValue).Average(n => n.HrvMeanMs!.Value)
                                  : null,
            TotalHypoCount    = nights.Sum(n => n.HypoCount),
            NightsWithHypoPct = nights.Count > 0 ? nights.Count(n => n.HypoCount > 0) * 100.0 / nights.Count : 0,
            ReferenceRanges   = referenceRanges,
            Last7dVsPrior7d = new SleepTrendsDelta
            {
                ScoreDelta       = l7Score.HasValue && p7Score.HasValue ? l7Score - p7Score : null,
                TirDelta         = l7Tir.HasValue   && p7Tir.HasValue   ? l7Tir   - p7Tir   : null,
                DeepMinutesDelta = l7Deep.HasValue  && p7Deep.HasValue  ? l7Deep  - p7Deep  : null,
                DawnRiseDelta    = l7Dawn.HasValue && p7Dawn.HasValue ? l7Dawn - p7Dawn : null,
            },
        };
    }

    // ── Weekly Summaries ──────────────────────────────────────────────────

    /// <summary>
    /// Noon-rule display day for a night, taken from the authoritative
    /// <see cref="SleepNightSummary.DisplayDate"/> (timezone-aware, set by
    /// <see cref="ComputeNightSummary"/>) so weekly bucketing agrees with
    /// <see cref="NightDate"/>. Falls back to the timezone-naive UTC noon rule only
    /// for summaries constructed without a DisplayDate (e.g. test fixtures).
    /// </summary>
    private static DateTime DisplayDay(SleepNightSummary night) =>
        DateOnly.TryParseExact(night.DisplayDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToDateTime(TimeOnly.MinValue)
            : night.InBedAt.AddHours(-12).Date;

    /// <summary>Monday on or before <paramref name="day"/>.</summary>
    private static DateTime WeekStartOf(DateTime day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    internal static IReadOnlyList<SleepWeekSummary> ComputeWeekSummaries(
        IReadOnlyList<SleepNightSummary> nights, DateTime from, DateTime to)
    {
        var rangeStart = from.Date;
        var rangeEnd   = to.Date;
        if (rangeEnd < rangeStart)
            return [];

        var nightsByWeek = nights
            .GroupBy(n => WeekStartOf(DisplayDay(n)))
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.InBedAt).ToList());

        // A night starting shortly after midnight on the range's first day displays
        // under the previous day, which can fall in the week before the range —
        // widen the loop bounds to any such spillover weeks.
        var firstWeek = WeekStartOf(rangeStart);
        var lastWeek  = WeekStartOf(rangeEnd);
        if (nightsByWeek.Count > 0)
        {
            var minNightWeek = nightsByWeek.Keys.Min();
            var maxNightWeek = nightsByWeek.Keys.Max();
            if (minNightWeek < firstWeek) firstWeek = minNightWeek;
            if (maxNightWeek > lastWeek)  lastWeek  = maxNightWeek;
        }

        var weeks = new List<SleepWeekSummary>();
        for (var weekStart = firstWeek; weekStart <= lastWeek; weekStart = weekStart.AddDays(7))
        {
            var weekEnd      = weekStart.AddDays(6);
            var overlapStart = weekStart > rangeStart ? weekStart : rangeStart;
            var overlapEnd   = weekEnd   < rangeEnd   ? weekEnd   : rangeEnd;
            var overlapDays  = overlapEnd >= overlapStart ? (int)(overlapEnd - overlapStart).TotalDays + 1 : 0;
            var weekNights   = nightsByWeek.TryGetValue(weekStart, out var list) ? list : [];

            var scored    = weekNights.Where(n => n.SleepScore.HasValue).ToList();
            var tirNights = weekNights.Where(n => n.OvernightTirPct.HasValue).ToList();

            weeks.Add(new SleepWeekSummary
            {
                WeekStart         = weekStart,
                WeekEnd           = weekEnd,
                Label             = $"{weekStart:MMM d} – {weekEnd:MMM d}",
                NightCount        = weekNights.Count,
                // Floored at NightCount so spillover weeks never report more nights than days.
                DaysInRange       = Math.Max(overlapDays, weekNights.Count),
                MeanAsleepMinutes = weekNights.Count > 0 ? weekNights.Average(n => n.SleepMinutes) : 0,
                MeanScore         = scored.Count    > 0 ? scored.Average(n => (double)n.SleepScore!.Value) : null,
                MeanTirPct        = tirNights.Count > 0 ? tirNights.Average(n => n.OvernightTirPct!.Value) : null,
                TotalHypoCount    = weekNights.Sum(n => n.HypoCount),
                SessionIds        = weekNights.Select(n => n.SessionId).ToList(),
            });
        }

        return weeks;
    }
}
