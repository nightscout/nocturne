using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Seeding;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>
/// The demo host's generated history, which no archetype may change: it is the one person the
/// demo has always shown, and its realtime stream continues from this history. Each digest was
/// taken before any archetype existed and must hold for a seed with none.
/// </summary>
public class DemoHostTimelineTests
{
    private static readonly DemoClock Sydney = new(
        TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));

    private static readonly DateTime End = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    private static List<DemoTimeStep> Timeline() => new DemoDataGenerator(
            Options.Create(SampleDataSeeder.ConfigurationFor(5)),
            Sydney,
            NullLogger<DemoDataGenerator>.Instance,
            NullLoggerFactory.Instance)
        .GenerateHistoricalTimeline(End)
        .ToList();

    [Fact]
    public void The_host_timeline_of_a_seed_is_the_one_it_has_always_been()
    {
        var text = new StringBuilder();
        foreach (var step in Timeline())
        {
            text.Append(CultureInfo.InvariantCulture, $"{step.Entry.Mills}|{step.Entry.Sgv}|{step.Entry.Direction}|{step.Loop}|{step.Scenario};");
            foreach (var treatment in step.Treatments)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"{treatment.EventType}|{treatment.Mills}|{treatment.Insulin}|{treatment.Carbs}|{treatment.Rate};");
            }
        }

        Digest(text).Should().Be("61B30DC08C53272274E59100D56D2152CB1CF91B489E0951A2D062043BF507D4");
    }

    [Fact]
    public void The_host_records_of_a_seed_are_the_ones_they_have_always_been()
    {
        var config = SampleDataSeeder.ConfigurationFor(5);
        var clock = SeedingClock.Resolve("Australia/Sydney", TimeProvider.System);
        var text = new StringBuilder();
        foreach (var step in Timeline())
        {
            var records = SampleDataSeeder.RecordsOf(step, clock, config, "demo-service");
            foreach (var entry in records.Entries)
                text.Append(CultureInfo.InvariantCulture, $"{entry.Id}|{entry.Type}|{entry.Device}|{entry.Mills}|{entry.Sgv}|{entry.DataSource};");
            foreach (var treatment in records.Treatments)
                text.Append(CultureInfo.InvariantCulture, $"{treatment.EventType}|{treatment.Mills}|{treatment.DataSource};");
            if (records.Status is { } status)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"{status.Id}|{status.Mills}|{status.Pump?.Reservoir}|{status.Pump?.Battery?.Percent}|{status.OpenAps?.Suggested?.Bg};");
            }
        }

        Digest(text).Should().Be("5913B8C630DF6700B216AA73CB25DD29B0BCE188D427A3E6E172F25B0312D0D8");
    }

    [Fact]
    public void The_host_days_are_the_ones_they_have_always_been()
    {
        var text = new StringBuilder();
        var first = new DateTime(2026, 6, 1);
        for (var d = 0; d < 120; d++)
            text.Append(CultureInfo.InvariantCulture, $"{DayScenarios.For(first.AddDays(d))};");

        for (var d = 0; d < 8; d++)
        {
            var day = first.AddDays(d);
            var (heartRates, steps) = DemoHealthDataGenerator.GenerateDailyActivity(day, "demo-service", Sydney);
            var (workoutStart, workoutEnd) = DemoHealthDataGenerator.WorkoutWindowFor(day);
            text.Append(CultureInfo.InvariantCulture, $"{workoutStart:o}|{workoutEnd:o};");
            foreach (var heartRate in heartRates)
                text.Append(CultureInfo.InvariantCulture, $"{heartRate.Timestamp:o}|{heartRate.Bpm};");
            foreach (var step in steps)
                text.Append(CultureInfo.InvariantCulture, $"{step.Timestamp:o}|{step.Metric};");

            var sleep = DemoHealthDataGenerator.GenerateSleepSession(day, "demo-service", Sydney);
            text.Append(CultureInfo.InvariantCulture,
                $"{sleep.StartTime:o}|{sleep.EndTime:o}|{sleep.DeepSleepMs}|{sleep.LightSleepMs}|{sleep.RemSleepMs}|{sleep.AvgHeartRate};");
        }

        foreach (var change in DemoDeviceLifecycle.GenerateSchedule(End, 30, Sydney))
            text.Append(CultureInfo.InvariantCulture, $"{change.EventType}|{change.TimestampUtc:o};");
        foreach (var span in DemoLifestyleSeeds.BuildSpans(End.Date, 30, End))
            text.Append(CultureInfo.InvariantCulture, $"{span.Category}|{span.State}|{span.StartLocal:o}|{span.EndLocal:o};");
        foreach (var interruption in Enumerable.Range(0, 10).SelectMany(d => DemoLoopInterruptions.On(End.Date.AddDays(-d), End)))
            text.Append(CultureInfo.InvariantCulture, $"{interruption.State}|{interruption.Start:o}|{interruption.End:o};");

        Digest(text).Should().Be("A2867F0C5C4FF0CAAF4AFE4160A3D875262366555494169D439F67DCD94F91E6");
    }

    private static string Digest(StringBuilder text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
}
