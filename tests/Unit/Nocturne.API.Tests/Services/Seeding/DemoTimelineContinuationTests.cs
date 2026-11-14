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
    /// Afternoon ends, when the phone gaps and manual mode the seed keeps clear of its end are likely,
    /// on several days so some of them fall in that quiet window.
    /// </summary>
    public static TheoryData<DateTimeOffset> SeedEnds()
    {
        var data = new TheoryData<DateTimeOffset>();
        for (var day = 0; day < 8; day++)
            data.Add(new DateTimeOffset(2026, 10, 5 + day, 4, 3 + 7 * day, 17, TimeSpan.Zero));
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

        var end = seedEnd.ToUnixTimeMilliseconds();
        continued.Take(seed.Count).Select(s => Fingerprint(s, end)).Should().Equal(seed.Select(s => Fingerprint(s, end)));
        continued.Skip(seed.Count - 1).Zip(continued.Skip(seed.Count), (a, b) => b.Utc - a.Utc)
            .Should().OnlyContain(gap => gap == TimeSpan.FromMinutes(5), "the timeline runs on past the seed's end");
        continued[^1].Utc.Should().BeAfter(seedEnd.UtcDateTime.AddHours(23));
    }

    /// <summary>
    /// A step as the chart, the loop and the treatment history read it. The seed's last step stops
    /// issuing treatments at its end, and the continued one does not, so treatments are compared up to it.
    /// </summary>
    private static string Fingerprint(DemoTimeStep step, long endMills) =>
        $"{step.Utc:O} {step.Entry.Sgv} {step.Loop} {step.Iob:F4} {step.Cob:F4} {step.TempBasalRate} "
        + string.Join(",", step.Treatments.Where(t => t.Mills < endMills).Select(t => $"{t.EventType}@{t.Mills}:{t.Insulin}/{t.Carbs}"));
}
