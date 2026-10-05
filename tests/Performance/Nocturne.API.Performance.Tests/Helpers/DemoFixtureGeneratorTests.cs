using Nocturne.Performance.Tests.Shared;
using Xunit;

namespace Nocturne.API.Performance.Tests.Helpers;

public class DemoFixtureGeneratorTests
{
    [Fact]
    public void GenerateSensorGlucose_PreservesCountAndUtcWindow()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var records = DemoFixtureGenerator.GenerateSensorGlucose(300, start).ToList();

        Assert.Equal(300, records.Count);
        Assert.Equal(start, records[0].Timestamp);
        Assert.Equal(start.AddMinutes(5 * 299), records[^1].Timestamp);
        Assert.All(records, record => Assert.Equal(DateTimeKind.Utc, record.Timestamp.Kind));
    }

    [Fact]
    public void GenerateTempBasals_PreservesDenseCountAndUtcWindow()
    {
        var start = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var records = DemoFixtureGenerator.GenerateTempBasals(288, start).ToList();

        Assert.Equal(288, records.Count);
        Assert.Equal(start, records[0].StartTimestamp);
        Assert.Equal(start.AddMinutes(5 * 287), records[^1].StartTimestamp);
        Assert.All(records, record => Assert.Equal(DateTimeKind.Utc, record.StartTimestamp.Kind));
    }

    [Fact]
    public void GenerateBoluses_PreservesTimelineTimestampAndRepeatableTherapyValues()
    {
        var start = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = DemoFixtureGenerator.GenerateTreatments(90)
            .Where(treatment => treatment.Insulin.HasValue && treatment.EventType is not "Temp Basal")
            .OrderBy(treatment => treatment.Mills)
            .Take(64)
            .ToList();

        var first = DemoFixtureGenerator.GenerateBoluses(64, start);
        var second = DemoFixtureGenerator.GenerateBoluses(64, start);

        Assert.Equal(64, first.Count);
        Assert.Equal(expected.Select(treatment => DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime),
            first.Select(bolus => bolus.Timestamp));
        Assert.Equal(first.Select(bolus => (bolus.Timestamp, bolus.Insulin, bolus.Kind)),
            second.Select(bolus => (bolus.Timestamp, bolus.Insulin, bolus.Kind)));
        Assert.All(first, bolus => Assert.InRange(bolus.Timestamp, start, start.AddDays(90)));
    }

    [Fact]
    public void StressProjections_PreserveRequestedCountsAndDocumentedSpacing()
    {
        var start = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boluses = DemoFixtureGenerator.GenerateBolusStressProjection(4_320, start);
        var carbs = DemoFixtureGenerator.GenerateCarbStressProjection(900, start);
        var demoBoluses = DemoFixtureGenerator.GenerateBoluses(4_320, start);
        var demoCarbs = DemoFixtureGenerator.GenerateCarbIntakes(900, start);
        var repeatedBoluses = DemoFixtureGenerator.GenerateBolusStressProjection(4_320, start);
        var repeatedCarbs = DemoFixtureGenerator.GenerateCarbStressProjection(900, start);

        Assert.Equal(4_320, boluses.Count);
        Assert.Equal(900, carbs.Count);
        Assert.Equal(demoBoluses.Select(bolus => bolus.Insulin), boluses.Select(bolus => bolus.Insulin));
        Assert.Equal(demoCarbs.Select(carb => carb.Carbs), carbs.Select(carb => carb.Carbs));
        Assert.Equal(boluses.Select(bolus => bolus.Insulin), repeatedBoluses.Select(bolus => bolus.Insulin));
        Assert.Equal(carbs.Select(carb => carb.Carbs), repeatedCarbs.Select(carb => carb.Carbs));
        Assert.Equal(start.AddMinutes(30), boluses[1].Timestamp);
        Assert.Equal(start.AddMinutes(30), carbs[1].Timestamp);
        Assert.Equal(start.AddMinutes(30 * 4_319), boluses[^1].Timestamp);
        Assert.Equal(start.AddMinutes(30 * 899), carbs[^1].Timestamp);
        Assert.Equal(2_160, boluses.Count(bolus => bolus.Kind == Nocturne.Core.Models.V4.BolusKind.Manual));
        Assert.Equal(2_160, boluses.Count(bolus => bolus.Kind == Nocturne.Core.Models.V4.BolusKind.Algorithm));
        Assert.Equal(2_160, boluses.Count(bolus => !bolus.Automatic));
        Assert.Equal(2_160, boluses.Count(bolus => bolus.Automatic));
    }

    [Fact]
    public void GenerateBoluses_ExtendsTheTimelineForLargePhysiologicalCounts()
    {
        var start = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var records = DemoFixtureGenerator.GenerateBoluses(4_321, start);

        Assert.Equal(4_321, records.Count);
        Assert.True(records[^1].Timestamp > start.AddDays(90));
        Assert.Equal(records.OrderBy(record => record.Timestamp).Select(record => record.Timestamp),
            records.Select(record => record.Timestamp));
        Assert.All(records, record => Assert.InRange(record.Timestamp, start, records[^1].Timestamp));
    }
}
