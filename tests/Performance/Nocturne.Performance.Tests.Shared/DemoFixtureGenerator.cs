using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;

namespace Nocturne.Performance.Tests.Shared;

public static class DemoFixtureGenerator
{
    private static readonly ConcurrentDictionary<(long Ticks, DateTimeKind Kind, int Days, int Seed), Lazy<List<Treatment>>>
        TreatmentCache = new();

    public static IEnumerable<SensorGlucose> GenerateSensorGlucose(
        int count,
        DateTime startDate,
        int randomSeed = 42)
    {
        var days = (int)Math.Ceiling(count / 288d);
        var generator = CreateGenerator(days, randomSeed);
        var endDate = startDate.AddDays(days);

        return generator.GenerateHistoricalTimeline(endDate)
            .Take(count)
            .Select(step => ToSensorGlucose(step.Entry));
    }

    public static SensorGlucose ToSensorGlucose(Entry entry) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime,
        Mgdl = entry.Sgv ?? entry.Mgdl,
        Direction = Enum.TryParse<GlucoseDirection>(entry.Direction, out var direction) ? direction : null,
        Delta = entry.Delta,
        Noise = entry.Noise,
        Filtered = entry.Filtered,
        Unfiltered = entry.Unfiltered,
    };

    public static IEnumerable<Treatment> GenerateTreatments(int days, int randomSeed = 42)
    {
        return GenerateTreatments(
            days,
            new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            randomSeed);
    }

    public static List<Bolus> GenerateBoluses(int count, DateTime startDate)
    {
        return GenerateTreatmentsWithCount(count, startDate, IsBolus)
            .Select(treatment => new Bolus
            {
                Id = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
                Insulin = treatment.Insulin!.Value,
                BolusType = Nocturne.Core.Models.V4.BolusType.Normal,
                Kind = treatment.EventType == "SMB" || treatment.EnteredBy == "demo-pump"
                    ? BolusKind.Algorithm
                    : BolusKind.Manual,
                Automatic = treatment.EventType == "SMB" || treatment.EnteredBy == "demo-pump",
            })
            .ToList();
    }

    public static List<CarbIntake> GenerateCarbIntakes(int count, DateTime startDate)
    {
        return GenerateTreatmentsWithCount(count, startDate, treatment => treatment.Carbs.HasValue)
            .Select(treatment => new CarbIntake
            {
                Id = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
                Carbs = treatment.Carbs!.Value,
            })
            .ToList();
    }

    public static List<Bolus> GenerateBolusStressProjection(int count, DateTime startDate, int minutesBetween = 30)
    {
        var demoBoluses = GenerateBoluses(count, startDate);
        return demoBoluses.Select((bolus, index) =>
        {
            var automatic = index % 2 == 1;
            bolus.Timestamp = startDate.AddMinutes(index * minutesBetween);
            bolus.Kind = automatic ? BolusKind.Algorithm : BolusKind.Manual;
            bolus.Automatic = automatic;
            return bolus;
        }).ToList();
    }

    public static List<CarbIntake> GenerateCarbStressProjection(int count, DateTime startDate, int minutesBetween = 30)
    {
        return GenerateCarbIntakes(count, startDate)
            .Select((carbIntake, index) =>
            {
                carbIntake.Timestamp = startDate.AddMinutes(index * minutesBetween);
                return carbIntake;
            })
            .ToList();
    }

    public static IEnumerable<TempBasal> GenerateTempBasals(
        int count,
        DateTime startDate,
        int randomSeed = 42)
    {
        var days = (int)Math.Ceiling(count / 288d);
        var generator = CreateGenerator(days, randomSeed);
        var endDate = startDate.AddDays(days);

        return generator.GenerateHistoricalTimeline(endDate)
            .Take(count)
            .Select(step => new TempBasal
            {
                Id = Guid.CreateVersion7(),
                StartTimestamp = step.Time,
                EndTimestamp = step.Time.AddMinutes(5),
                Rate = step.TempBasalRate ?? 1.0,
                Origin = step.TempBasalRate.HasValue ? TempBasalOrigin.Algorithm : TempBasalOrigin.Scheduled,
            });
    }

    private static DemoDataGenerator CreateGenerator(int days, int randomSeed) => new(
        Options.Create(new DemoModeConfiguration { BackfillDays = days, RandomSeed = randomSeed }),
        NullLogger<DemoDataGenerator>.Instance,
        NullLoggerFactory.Instance);

    private static IEnumerable<Treatment> GenerateTreatments(int days, DateTime startDate, int randomSeed = 42)
    {
        var key = (startDate.Ticks, startDate.Kind, days, randomSeed);
        return TreatmentCache.GetOrAdd(key, _ => new Lazy<List<Treatment>>(() =>
        {
            var generator = CreateGenerator(days, randomSeed);
            return generator.GenerateHistoricalTimeline(startDate.AddDays(days))
                .SelectMany(step => step.Treatments)
                .ToList();
        })).Value;
    }

    private static IEnumerable<Treatment> GenerateTreatmentsWithCount(
        int count, DateTime startDate, Func<Treatment, bool> predicate)
    {
        if (count <= 0)
            return [];

        var days = 90;
        List<Treatment> events;
        while (true)
        {
            events = GenerateTreatments(days, startDate)
                .Where(predicate)
                .OrderBy(treatment => treatment.Mills)
                .ToList();
            if (events.Count >= count)
                return events.Take(count);
            days = checked(days * 2);
        }
    }

    private static bool IsBolus(Treatment treatment) =>
        treatment.Insulin.HasValue && treatment.EventType is not "Temp Basal";
}
