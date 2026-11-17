using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// The live sample-data feed writes a continued timeline after the records a seed wrote, so the
/// continuation has to repeat the seed exactly or the chart jumps where they meet.
/// </summary>
public class DemoTimelineContinuationTests
{
    private const int Days = 4;

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    /// <summary>
    /// Ends at a different local time each day, so some fall in the quiet window the seed keeps clear
    /// of interruptions and some split a step that issues a meal, a correction or a fingerstick.
    /// </summary>
    public static TheoryData<DateTimeOffset> SeedEnds()
    {
        var data = new TheoryData<DateTimeOffset>();
        var first = new DateTimeOffset(2026, 10, 5, 0, 3, 17, TimeSpan.Zero);
        for (var day = 0; day < 48; day++)
            data.Add(first.AddDays(day).AddMinutes(day * 31));
        return data;
    }

    private static DemoDataGenerator Generator(DateTimeOffset now)
    {
        var time = new FakeTimeProvider(now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return new DemoDataGenerator(
            Options.Create(new DemoModeConfiguration { BackfillDays = Days, RandomSeed = 1 }),
            new DemoClock(time, Zone),
            NullLogger<DemoDataGenerator>.Instance,
            NullLoggerFactory.Instance);
    }

    [Theory]
    [MemberData(nameof(SeedEnds))]
    public void The_continued_timeline_repeats_the_seed_then_carries_on(DateTimeOffset seedEnd)
    {
        var seed = Generator(seedEnd).GenerateHistoricalTimeline().ToList();
        var firstDay = seed[0].Time.Date;

        var continued = Generator(seedEnd.AddDays(2))
            .ContinueTimeline(firstDay, seedEnd.UtcDateTime)
            .TakeWhile(s => s.Utc < seedEnd.UtcDateTime.AddDays(1))
            .ToList();

        continued.Take(seed.Count).Select(Fingerprint).Should().Equal(seed.Select(Fingerprint));
        continued.Skip(seed.Count - 1).Zip(continued.Skip(seed.Count), (a, b) => b.Utc - a.Utc)
            .Should().OnlyContain(gap => gap == TimeSpan.FromMinutes(5), "the timeline runs on past the seed's end");
        continued[^1].Utc.Should().BeAfter(seedEnd.UtcDateTime.AddHours(23));
    }

    [Fact]
    public void A_seed_that_ends_inside_a_step_is_repeated_up_to_its_end()
    {
        // Treatments off the five-minute grid (meals, their boluses, notes): ending the seed just
        // before one leaves it to the step after the seed's last.
        var reference = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var splitEnds = Generator(reference).GenerateHistoricalTimeline()
            .SelectMany(s => s.Treatments)
            .Where(t => t.Mills % 300_000 > 1_000)
            .Select(t => DateTimeOffset.FromUnixTimeMilliseconds(t.Mills - 1_000))
            .Where(end => end < reference.AddDays(-1))
            .DistinctBy(end => end.ToUnixTimeMilliseconds() / 300_000)
            .Take(30)
            .ToList();

        splitEnds.Should().HaveCountGreaterThan(10);
        foreach (var seedEnd in splitEnds)
            The_continued_timeline_repeats_the_seed_then_carries_on(seedEnd);
    }

    /// <summary>
    /// A step as the chart, the loop and the treatment history read it.
    /// </summary>
    private static string Fingerprint(DemoTimeStep step) =>
        $"{step.Utc:O} {step.Entry.Sgv} {step.Loop} {step.Iob:F4} {step.Cob:F4} {step.TempBasalRate} "
        + string.Join(",", step.Treatments.Select(t => $"{t.EventType}@{t.Mills}:{t.Insulin}/{t.Carbs}"))
        + " " + string.Join(",", step.ExtraEntries.Select(e => $"{e.Type}@{e.Mills}:{e.Mgdl}"));
}
