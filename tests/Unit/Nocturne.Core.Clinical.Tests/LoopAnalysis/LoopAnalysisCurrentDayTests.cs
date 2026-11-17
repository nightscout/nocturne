using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

/// <summary>
/// A week resolved partway through its last day, with the loop and the CGM running without a break
/// up to now: no page may read the part of today still to come as time without them.
/// </summary>
public class LoopAnalysisCurrentDayTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;

    // 06:20 on 2024-06-10 in Sydney (UTC+10, no DST in June).
    private static readonly DateTime Now = new(2024, 6, 9, 20, 20, 0, DateTimeKind.Utc);

    private static readonly LoopAnalysisResolvedRange Range =
        LoopAnalysisRanges.Resolve(new DateOnly(2024, 6, 4), new DateOnly(2024, 6, 10), Sydney, 90, Now);

    private static long From => new DateTimeOffset(Range.From).ToUnixTimeMilliseconds();
    private static long To => new DateTimeOffset(Range.To).ToUnixTimeMilliseconds();

    private static LoopAnalysisMask Mask() =>
        LoopAnalysisMask.Build(new LoopAnalysisMasks(), From, To, Sydney, new LoopAnalysisMaskSources());

    private static IEnumerable<long> EveryFiveMinutes()
    {
        for (var t = From; t < To; t += 5 * Minute)
            yield return t;
    }

    [Fact]
    public void LoopHealth_ReadsFullCoverageUpToNow_InEveryHourAndOnEveryDay()
    {
        var inputs = new LoopHealthInputs
        {
            Zone = Sydney,
            Cycles = [.. EveryFiveMinutes().Select(t => new ApsLoopCycle
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime,
                AidAlgorithm = AidAlgorithm.Trio,
                Enacted = true,
                EnactedRate = 1.0,
                HasReason = true,
                SuggestedChange = true,
            })],
            Insulin = new NetInsulinAboveSchedule([], [], []),
            Evidence = new BasalEvidence(IntervalSet.Empty, IntervalSet.Empty, IntervalSet.Empty),
            Glucose = [.. EveryFiveMinutes().Select(t => new SensorGlucose
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime,
                Mgdl = 120,
            })],
        };

        var result = LoopHealthCalculator.Calculate(inputs, Mask(), From, To);

        foreach (var share in new[] { result.Activity.ClosedLoop!, result.Activity.Cgm! })
        {
            share.Percent.Should().Be(100);
            share.Daily!.P10.Should().Be(100);
            share.Hours.Should().OnlyContain(h => h.Percent == 100, "no hour loses the part of today still to come");
            share.Hours.Single(h => h.Hour == 12).Daily!.N.Should().Be(6, "today's noon has not happened");
            share.Hours.Single(h => h.Hour == 5).Daily!.N.Should().Be(7);
        }
    }

    [Fact]
    public void DailyProfile_CountsOnlyTheSlotsOfTodayThatHaveHappened()
    {
        var doses = Enumerable.Range(0, 7)
            .Select(d => From + d * 24 * Hour + 12 * Hour)
            .Where(t => t < To)
            .Select(t => new InsulinDose(t, 1, false));
        var inputs = new DailyProfileInputs
        {
            Zone = Sydney,
            Insulin = new NetInsulinAboveSchedule([], [], doses),
            Glucose = [.. EveryFiveMinutes().Select(t => new GlucoseSample(t, 120))],
        };

        var result = DailyProfileCalculator.Calculate(inputs, Mask(), From, To);

        var noon = result.Slots[12 * 60 / DailyProfileCalculator.SlotMinutes];
        noon.ManualBoluses!.EventsPerDay.Should().Be(1, "a bolus at noon every day that has had a noon");
        noon.ManualBoluses.N.Should().Be(6);
        result.Slots[6 * 60 / DailyProfileCalculator.SlotMinutes].ManualBoluses!.N.Should().Be(7);
        result.Days.Should().HaveCount(7);
        result.Days.Select(d => d.Complete).Should().Equal(true, true, true, true, true, true, false);
        result.Days.Should().OnlyContain(d => d.Coverage.Analysed == d.Coverage.Glucose,
            "no slot is analysed and without glucose, today's included");
    }
}
