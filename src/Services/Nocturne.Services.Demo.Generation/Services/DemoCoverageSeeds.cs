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
    /// A failing-sensor day for <em>Erroneous readings</em>. Lunch is at <see cref="Lunch"/>, with
    /// the bolus on time, and <see cref="OnsetMinutes"/> later the primary sensor goes through
    /// <see cref="Phases"/>. Finger sticks at <see cref="FingerstickMinutes"/> from the onset show the
    /// real value. It falls on <see cref="Day"/> every other week, in the weeks whose parity is
    /// <see cref="WeekParity"/>.
    /// </summary>
    /// <remarks>
    /// The real value is pulled to <see cref="RealLevel"/> through the false stretches (see
    /// <see cref="AnchoredReal"/>), so the readings and the finger sticks land where the day intends
    /// whatever the generated trace does there.
    /// </remarks>
    public sealed record PlantedSensorError(
        DayOfWeek Day, int WeekParity, TimeOnly Lunch, int OnsetMinutes, double RealLevel,
        IReadOnlyList<SensorErrorPhase> Phases, int[] FingerstickMinutes)
    {
        public TimeOnly Start => Lunch.AddMinutes(OnsetMinutes);
    }

    /// <summary>
    /// One stretch of a failing-sensor day. The sensor reads the real value plus an offset that moves
    /// from <see cref="OffsetFrom"/> to <see cref="OffsetTo"/> through the stretch (0 reads true).
    /// <see cref="Jitter"/> is the share of the full jumps from reading to reading, and <see cref="Flags"/> marks which
    /// readings carry the flag, <c>x</c> for invalid.
    /// </summary>
    public sealed record SensorErrorPhase(
        string Name, int Minutes, double OffsetFrom, double OffsetTo, double Jitter, string Flags)
    {
        public bool ReadsTrue => OffsetFrom == 0 && OffsetTo == 0;
    }

    /// <summary>
    /// A false low with the shape of a real G7 failure, the most common kind: a finger stick reads
    /// well above the sensor, about 60 mg/dL. About an hour after lunch, over a real 125, the
    /// reading plunges in two steps, then jumps about some 60 below it for two hours, with seven
    /// readings in ten flagged and a quiet gap that splits it into two episodes. It recovers over an
    /// hour, reads true for two hours, and in the evening drifts low again with a single flag: a
    /// false hypo the sensor didn't report, which only the finger stick shows.
    /// </summary>
    private static readonly IReadOnlyList<SensorErrorPhase> FalseLowPhases =
    [
        new("Plunge", 10, -35, -60, 0.4, ".x"),
        new("Failing", 115, -60, -60, 1, "xxxxxxxxx.xxxx.....x.xx"),
        new("Recovering", 60, -30, -10, 0.3, "........x..."),
        new("Quiet", 120, 0, 0, 0, "."),
        new("Drifting", 70, -50, -65, 0.2, "......x......."),
    ];

    /// <summary>
    /// A false high, rarer than a false low: two hours after lunch the sensor drifts up and then
    /// holds a hyper of about 210 that doesn't come down, peaking near 230, some 60 mg/dL above the
    /// finger sticks. It flags part of it, then drops back to the real value.
    /// </summary>
    private static readonly IReadOnlyList<SensorErrorPhase> FalseHighPhases =
    [
        new("Drift", 20, 10, 60, 0.2, "...."),
        new("Stuck", 120, 60, 60, 0.8, "..xxxx..xxx.xxxxx...x..."),
        new("Release", 15, 30, 10, 0.3, "..."),
    ];

    /// <summary>
    /// A false low on a Monday afternoon and a false high on a Thursday afternoon, in alternate weeks,
    /// so any two weeks hold one of each. The finger sticks fall where the jumps are small, so each
    /// shows about the day's offset.
    /// </summary>
    public static readonly IReadOnlyList<PlantedSensorError> PlantedSensorErrors =
    [
        new(DayOfWeek.Monday, 1, new TimeOnly(14, 0), 55, 125, FalseLowPhases, [30, 70, 120, 330]),
        new(DayOfWeek.Thursday, 0, new TimeOnly(12, 30), 100, 150, FalseHighPhases, [50, 120]),
    ];

    /// <summary>The failing-sensor day that falls on <paramref name="day"/>, if one does.</summary>
    public static PlantedSensorError? SensorErrorOn(DateTime day) =>
        PlantedSensorErrors.FirstOrDefault(e => day.DayOfWeek == e.Day && WeekParity(day) == e.WeekParity);

    /// <summary>Step jumps of a failing sensor, mg/dL, repeated through the day.</summary>
    private static readonly int[] SensorErrorJitter = [0, -21, 14, -9, 26, -17, 5, 19, -24, 11, -6, 23, -13, 8];

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

        if (AnchoredReal(step.Time, step.Entry.GlucoseMgdl()) is { } anchored)
        {
            step.Entry.Mgdl = anchored;
            step.Entry.Sgv = anchored;
        }

        if (SensorErrorAt(step.Time) is var (error, phase, minute) && !phase.ReadsTrue)
        {
            var real = step.Entry.GlucoseMgdl();
            var into = minute - PhaseStart(error, phase);
            var steps = Math.Max(1, phase.Minutes / 5 - 1);
            var offset = phase.OffsetFrom + (phase.OffsetTo - phase.OffsetFrom) * Math.Min(1, into / 5 / (double)steps);
            var jump = SensorErrorJitter[minute / 5 % SensorErrorJitter.Length] * phase.Jitter;
            var reported = Math.Round(Math.Clamp(real + offset + jump, 40, 400));
            step.Entry.Mgdl = reported;
            step.Entry.Sgv = reported;
            if (phase.Flags[into / 5 % phase.Flags.Length] == 'x')
            {
                step.Entry.AdditionalProperties = new Dictionary<string, object>
                {
                    ["quality"] = "Invalid",
                    ["sensorStatus"] = "G7:Errors",
                };
            }
            return error.FingerstickMinutes.Contains(minute)
                ? [step.Entry, CreateFingerstick(step.Entry, real)]
                : [step.Entry];
        }

        return step.Time.Minute % 10 == 0
            ? [step.Entry, CreateComparisonReading(step.Entry)]
            : [step.Entry];
    }

    /// <summary>
    /// The finger sticks among <paramref name="readings"/> (from <see cref="CgmReadings"/>) as BG
    /// Check treatments as well, as xDrip uploads them, so the chart marks them.
    /// </summary>
    public static IEnumerable<Treatment> FingerstickTreatments(IEnumerable<Entry> readings) =>
        readings.Where(entry => entry.Type == "mbg").Select(entry => new Treatment
        {
            EventType = "BG Check",
            Glucose = entry.Mbg,
            GlucoseType = "Finger",
            Units = "mg/dl",
            Mills = entry.Mills,
            Created_at = entry.CreatedAt,
            EnteredBy = "demo-user",
            DataSource = entry.DataSource,
        });

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

    /// <summary>
    /// The failing-sensor day running at <paramref name="time"/>, its phase, and the minutes since
    /// the day's <see cref="PlantedSensorError.Start"/>.
    /// </summary>
    public static (PlantedSensorError Error, SensorErrorPhase Phase, int Minute)? SensorErrorAt(DateTime time)
    {
        var clock = TimeOnly.FromDateTime(time);
        if (SensorErrorOn(time) is not { } error || clock < error.Start)
            return null;
        var minute = (int)(clock - error.Start).TotalMinutes;
        foreach (var phase in error.Phases)
        {
            if (minute < PhaseStart(error, phase) + phase.Minutes)
                return (error, phase, minute);
        }
        return null;
    }

    /// <summary>How long the real value takes to reach a failing day's level, and to leave it.</summary>
    public const int RealLevelRampMinutes = 30;

    /// <summary>
    /// The real value at <paramref name="time"/> on a failing-sensor day: <paramref name="real"/>
    /// pulled to <see cref="PlantedSensorError.RealLevel"/> through every false stretch, and eased in
    /// and out over <see cref="RealLevelRampMinutes"/> either side. Null where nothing is pulled.
    /// </summary>
    public static double? AnchoredReal(DateTime time, double real) =>
        SensorErrorOn(time) is { } error && RealLevelWeight(error, time) is var weight and > 0
            ? Math.Round(real + (error.RealLevel - real) * weight)
            : null;

    /// <summary>
    /// Whether <see cref="AnchoredReal"/> moves the real value at <paramref name="time"/>. The
    /// generator's own finger sticks skip that time, since they read the value it generated.
    /// </summary>
    public static bool IsAnchored(DateTime time) =>
        SensorErrorOn(time) is { } error && RealLevelWeight(error, time) > 0;

    private static double RealLevelWeight(PlantedSensorError error, DateTime time)
    {
        var minute = (time - time.Date.Add(error.Start.ToTimeSpan())).TotalMinutes;
        return error.Phases.Where(phase => !phase.ReadsTrue)
            .Select(phase =>
            {
                var from = PhaseStart(error, phase);
                var outside = Math.Max(from - minute, minute - (from + phase.Minutes - 5));
                return Math.Clamp(1 - outside / RealLevelRampMinutes, 0, 1);
            })
            .Max();
    }

    /// <summary>0 or 1, alternating by Monday-to-Sunday week.</summary>
    private static int WeekParity(DateTime time) => DateOnly.FromDateTime(time).DayNumber / 7 % 2;

    /// <summary>The minutes from the start of <paramref name="error"/> to <paramref name="phase"/>.</summary>
    public static int PhaseStart(PlantedSensorError error, SensorErrorPhase phase) =>
        error.Phases.TakeWhile(p => p != phase).Sum(p => p.Minutes);

    private static Entry CreateFingerstick(Entry source, double mgdl)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(source.Mills).UtcDateTime;
        return new Entry
        {
            Type = "mbg",
            Device = source.Device,
            Mills = source.Mills,
            Date = timestamp,
            DateString = timestamp.ToString("o"),
            Mgdl = Math.Round(mgdl),
            Mbg = Math.Round(mgdl),
            DataSource = source.DataSource,
            CreatedAt = timestamp.ToString("o"),
            ModifiedAt = timestamp,
        };
    }

    private static Entry CreateComparisonReading(Entry source)
    {
        var mills = source.Mills + 120_000;
        var timestamp = mills.ToUtcDateTime();
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
