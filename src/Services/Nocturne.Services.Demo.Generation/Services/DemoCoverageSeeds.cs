using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

public static class DemoCoverageSeeds
{
    public const string PrimaryCgmSerial = "demo-cgm";
    public const string ComparisonCgmSerial = "demo-cgm-secondary";

    public const double CompressionNadirMgdl = 58;

    private const double CompressionRecoveryMinutes = 10;

    public static bool IsCompressionNight(DateOnly night) =>
        night.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Tuesday or DayOfWeek.Thursday;

    /// <summary>The planted nadir after <paramref name="night"/>, at the demo's local wall clock.</summary>
    public static long CompressionNadirMills(DateOnly night, DemoClock clock) =>
        clock.ToUtc(CompressionNadirLocal(night)).ToMills();

    /// <summary>
    /// Whether <paramref name="night"/> carries a planted compression artifact whose last reading
    /// lies at or before <paramref name="utc"/>.
    /// </summary>
    public static bool CompressionArtifactEndedBy(DateOnly night, DemoClock clock, DateTime utc) =>
        IsCompressionNight(night)
        && clock.ToUtc(CompressionNadirLocal(night).AddMinutes(CompressionRecoveryMinutes)) <= utc;

    private static DateTime CompressionNadirLocal(DateOnly night) =>
        night.AddDays(1).ToDateTime(new TimeOnly(4, 10));

    /// <summary>
    /// The primary reading at <paramref name="step"/>, with the compression artifact applied to
    /// <c>step.Entry</c> in place, and the comparison reading when one is due. None while a new
    /// sensor warms up.
    /// </summary>
    /// <remarks>
    /// The detector reads both sensors as one stream and measures the drop and the recovery
    /// against the readings around the nadir, so the artifact pins the readings either side of
    /// it and the comparison sensor stays silent through it. Otherwise the random overnight
    /// trace decides whether the planted compression low is found.
    /// </remarks>
    public static IReadOnlyList<Entry> CgmReadings(DemoTimeStep step)
    {
        if (step.Loop == DemoLoopState.SensorWarmUp)
            return [];

        if (CompressionArtifactAt(step.Time) is { } artifact)
        {
            step.Entry.Mgdl = artifact;
            step.Entry.Sgv = artifact;
            return [step.Entry];
        }

        return step.Time.Minute % 10 == 0
            ? [step.Entry, CreateComparisonReading(step.Entry)]
            : [step.Entry];
    }

    private static double? CompressionArtifactAt(DateTime time)
    {
        var night = DateOnly.FromDateTime(time).AddDays(-1);
        if (!IsCompressionNight(night))
            return null;

        var minutesFromNadir = (time - CompressionNadirLocal(night)).TotalMinutes;
        return minutesFromNadir switch
        {
            >= -30 and <= -10 => 110,
            -5 => 90,
            0 => CompressionNadirMgdl,
            5 => 82,
            CompressionRecoveryMinutes => 108,
            _ => null,
        };
    }

    private static Entry CreateComparisonReading(Entry source)
    {
        var mills = source.Mills + 120_000;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        var variation = source.Mills / 300_000 % 5 - 2;
        var glucose = Math.Clamp(source.GlucoseMgdl() + variation, 40, 400);

        return new Entry
        {
            Type = "sgv",
            Device = ComparisonCgmSerial,
            Mills = mills,
            Date = timestamp,
            DateString = timestamp.ToString("o"),
            Mgdl = glucose,
            Sgv = glucose,
            Direction = source.Direction,
            Delta = source.Delta,
            DataSource = source.DataSource,
            CreatedAt = timestamp.ToString("o"),
            ModifiedAt = timestamp,
        };
    }
}
