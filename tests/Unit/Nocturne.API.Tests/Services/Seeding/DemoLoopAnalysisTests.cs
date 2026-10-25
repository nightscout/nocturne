using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.V4;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// The sample tenant's week read by the Daily profile and Settings evidence calculators, from the
/// inputs <c>LoopAnalysisService</c> loads: the basal series filled from the seeded schedule by
/// <see cref="BasalSeriesBuilder"/>, IOB and COB preferring the device statuses' own, and the
/// pump mode, override, temporary target and illness spans the seeder writes.
/// </summary>
/// <remarks>
/// The wiring is a copy of <c>LoopAnalysisService</c>'s loading, simpler in four ways, none of which
/// the sample's data reaches today: the series are not padded past the range; the scheduled baseline
/// carries no override scale factor; open loop comes from Manual pump mode spans alone, not Off; and
/// <see cref="TargetAt"/> reads the profile's targets without temporary targets.
/// </remarks>
public class DemoLoopAnalysisTests(ITestOutputHelper output)
{
    private const long Minute = 60_000;

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Local;

    /// <summary>
    /// A week with a sick day and every kind of loop interruption, manual mode among them, so the
    /// insulin totals test sees the days the masks take out and the days they leave in.
    /// </summary>
    private static readonly DateTime End = new(2026, 9, 7, 20, 0, 0);

    private static readonly Lazy<(SettingsEvidenceInputs Inputs, long From, long To)> Week = new(Load);

    private static (SettingsEvidenceInputs Inputs, long From, long To) Load()
    {
        var run = DemoLoopHealthTests.Simulate(End);
        var config = new DemoModeConfiguration();
        var from = Mills(run.Steps[0].Time);
        var to = Mills(run.Steps[^1].Time) + 5 * Minute;
        var treatments = run.Steps.SelectMany(s => s.Treatments).ToList();

        var boluses = treatments.Where(t => t.Insulin > 0).Select(t =>
        {
            var bolus = TreatmentDecomposer.MapToBolus(t, null);
            if (t.EventType == "SMB")
                bolus.Kind = BolusKind.Algorithm;
            return bolus;
        }).ToList();
        var carbs = treatments.Where(t => t.Carbs > 0).Select(t => TreatmentDecomposer.MapToCarbIntake(t, null)).ToList();
        var temps = run.Temps.Select(t => t.Temp).ToList();

        var schedule = Schedule(from, to, config.BasalRate);
        var segments = new Mock<IBasalSegmentService>();
        segments.Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((long start, long end, CancellationToken _) => Clip(schedule, start, end));
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var builder = new BasalSeriesBuilder(therapy.Object, segments.Object, NullLogger<BasalSeriesBuilder>.Instance);

        var profile = DemoTherapyProfile.BuildProfile(config, DateTime.UtcNow).Store[DemoTherapyProfile.ProfileName];
        static List<ScheduleEntry> Entries(IEnumerable<TimeValue> values) =>
            [.. values.Select(v => new ScheduleEntry { Time = v.Time, Value = v.Value, TimeAsSeconds = v.TimeAsSeconds })];
        var snapshot = new TherapySnapshot(
            profile.Dia, (int)config.InsulinPeakMinutes, profile.CarbsHr, Zone, null, 0,
            Entries(profile.Sens), Entries(profile.CarbRatio), Entries(profile.Basal));
        var timeline = new TherapyTimeline([new TherapySegment(from - DailyProfileCalculator.ActingMs, to + 1, snapshot)]);

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(temps, to);
        var scheduled = new ScheduledBasal(timeline.BasalSegments(), []);
        var series = builder.BuildAsync(clipped, from, to, snapshot.BasalRateAt(from), []).GetAwaiter().GetResult();
        var iob = new IobCalculator(null!, null!, null!, null!, null!, null!);
        var cob = new CobCalculator(NullLogger<CobCalculator>.Instance, iob, null!, null!, null!, null!);

        var spans = DemoLifestyleSeeds.BuildSpans(End.Date, DemoLoopHealthTests.Days, End);
        List<MillsInterval> SpansOf(StateSpanCategory category, string? state = null) =>
        [
            .. spans.Where(s => s.Category == category && (state is null || s.State == state))
                .Select(s => new MillsInterval(Mills(s.StartLocal), s.EndLocal is { } end ? Mills(end) : to)),
        ];

        var inputs = new SettingsEvidenceInputs
        {
            Series = new DailyProfileInputs
            {
                Zone = Zone,
                Glucose = [.. run.Readings.Select(r => new GlucoseSample(r.Mills, r.Mgdl))],
                Insulin = new NetInsulinAboveSchedule(
                    NetInsulinAboveSchedule.DeliveredFromSeries(series, to, evidence),
                    scheduled.Segments(clipped),
                    boluses.Select(b => new InsulinDose(b.Mills, b.Insulin, b.Kind == BolusKind.Algorithm))),
                Carbs = [.. carbs.Select(c => new CarbEvent(c.Mills, c.Carbs))],
                IobCob = IobCobSeries.Build(
                    iob, cob, boluses, carbs, temps, timeline, scheduled,
                    DeviceReadings.Of(run.Snapshots.Select(s => new ApsIobCobPoint(s.Timestamp, s.Iob, s.Cob)), null),
                    from, to - 1, to - 1, 5 * Minute),
                CalculatedIobCobTime = DailyProfileCalculator.CalculableIobCobTime(
                    evidence, boluses.Select(b => b.Mills).Concat(carbs.Select(c => c.Mills))),
                TargetAt = mills => TargetAt(profile, mills),
            },
            Sources = new LoopAnalysisMaskSources
            {
                Overrides = SpansOf(StateSpanCategory.Override),
                TemporaryTargets = SpansOf(StateSpanCategory.TemporaryTarget),
                Illness = SpansOf(StateSpanCategory.Illness),
                OpenLoop = LoopAnalysisMask.OpenLoop(
                    SpansOf(StateSpanCategory.PumpMode, nameof(PumpModeState.Manual)), [], [], run.Cycles.Select(c => c.Mills)).Intervals,
            },
            Therapy = timeline,
        };
        return (inputs, from, to);
    }

    private static List<BasalSegment> Schedule(long from, long to, double baseRate)
    {
        var segments = new List<BasalSegment>();
        var start = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime.Date.AddDays(-1);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime.Date.AddDays(1);
        for (var day = start; day < end; day = day.AddDays(1))
        {
            var blocks = DemoTherapyProfile.BasalBlocks;
            for (var i = 0; i < blocks.Count; i++)
            {
                var blockStart = day + blocks[i].Start;
                var blockEnd = i + 1 < blocks.Count ? day + blocks[i + 1].Start : day.AddDays(1);
                var rate = DemoTherapyProfile.ScheduledRateAt(blockStart, baseRate);
                segments.Add(new BasalSegment(Mills(blockStart), Mills(blockEnd), rate, rate, DemoTherapyProfile.ProfileName));
            }
        }
        return segments;
    }

    private static async IAsyncEnumerable<BasalSegment> Clip(IEnumerable<BasalSegment> schedule, long from, long to)
    {
        await Task.CompletedTask;
        foreach (var s in schedule.Where(s => s.EndMills > from && s.StartMills < to))
            yield return s with { StartMills = Math.Max(s.StartMills, from), EndMills = Math.Min(s.EndMills, to) };
    }

    private static (double Low, double High)? TargetAt(ProfileData profile, long mills)
    {
        var seconds = (int)DateTimeOffset.FromUnixTimeMilliseconds(mills).LocalDateTime.TimeOfDay.TotalSeconds;
        static double At(List<TimeValue> values, int seconds) =>
            values.Last(v => (v.TimeAsSeconds ?? 0) <= seconds).Value;
        return (At(profile.TargetLow, seconds), At(profile.TargetHigh, seconds));
    }

    private static long Mills(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    private static LoopAnalysisMask Mask(long from, long to) =>
        LoopAnalysisMask.Build(new LoopAnalysisMasks(), from, to, Zone, Week.Value.Inputs.Sources);

    [Fact]
    public void SampleWeek_HasABlockWithAnObservedIsf()
    {
        var (inputs, from, to) = Week.Value;

        var report = SettingsEvidenceCalculator.Calculate(inputs, Mask(from, to), from, to);

        foreach (var block in report.Isf)
            output.WriteLine($"{block.Block}: n={block.N} observed={block.Observed} profile={block.Profile}");
        report.Isf.Should().Contain(b => b.N >= SettingsEvidenceCalculator.MinimumEpisodes && b.Observed != null);
    }

    /// <summary>
    /// The insulin totals add up only days whose delivery is known and analysed for
    /// <see cref="DailyProfileCalculator.InsulinSummaryMinimumCoverage"/> of the day. The sample's
    /// loop interruptions are short enough that every whole day counts, a day in manual mode among
    /// them, except the sick day, which the default illness mask takes out.
    /// </summary>
    [Fact]
    public void SampleWeek_EveryWholeDayCountsInTheInsulinTotals()
    {
        var (inputs, from, to) = Week.Value;
        var firstWholeDay = DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime.Date.AddDays(1);
        var lastWholeDay = DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime.Date;
        var (start, end) = (Mills(firstWholeDay), Mills(lastWholeDay));

        var profile = DailyProfileCalculator.Calculate(inputs.Series, Mask(start, end), start, end);

        var wholeDays = Enumerable.Range(0, (lastWholeDay - firstWholeDay).Days).Select(d => firstWholeDay.AddDays(d)).ToList();
        var sickDays = wholeDays.Count(d => DayScenarios.For(d) == DayScenario.SickDay);
        sickDays.Should().BePositive("the week holds a sick day for the illness mask to take out");
        wholeDays.Should().Contain(d => DemoLoopInterruptions.ManualModeWindow(d, End) != null, "the week holds a day in manual mode");
        profile.Summary.Insulin.Should().NotBeNull();
        profile.Summary.Insulin!.Total!.N.Should().Be(wholeDays.Count - sickDays);
    }
}
