using Nocturne.Services.Demo.Configuration;

namespace Nocturne.Services.Demo.Services;

/// <summary>How a demo person takes their insulin.</summary>
public enum DemoTherapy
{
    /// <summary>A pump a loop drives: temp basals, automatic boluses and an uploaded loop status.</summary>
    Loop,

    /// <summary>A pump the person drives: meal boluses, and the day's planned temp basals set by hand.</summary>
    Pump,

    /// <summary>Injections: meal and correction boluses only.</summary>
    Mdi,
}

/// <summary>What a demo person's data must show whenever someone looks.</summary>
[Flags]
public enum DemoBehaviour
{
    /// <summary>Nothing beyond the person's therapy and scenarios.</summary>
    None = 0,

    /// <summary>The person keeps their glucose in range: highs and lows are pulled back toward it.</summary>
    InRange = 1,

    /// <summary>The sensor stops reporting <see cref="DemoArchetype.SignalLostLag"/> before the end of the seeded range.</summary>
    SensorQuiet = 2,

    /// <summary>A low every day, at <see cref="DemoArchetype.DailyLowStart"/>.</summary>
    DailyLow = 4,

    /// <summary>A glucose that climbs for <see cref="DemoArchetype.RiseCycle"/> from each local midnight on, then falls back in one step.</summary>
    RisingTrend = 8,
}

/// <summary>
/// One demo person: the seed, the weights of the day scenarios, the therapy, the therapy settings and the
/// behaviour that make the person what their name says. The generator reads everything person-specific from here.
/// </summary>
/// <param name="Key">The <see cref="Nocturne.Core.Models.Demo.DemoClinicPerson.Key"/> of the person.</param>
/// <param name="Scenarios">How often each day scenario falls; a weight of zero never does.</param>
public sealed record DemoArchetype(
    string Key,
    int Seed,
    IReadOnlyList<(DayScenario Scenario, int Weight)> Scenarios,
    DemoTherapy Therapy,
    DemoBehaviour Behaviour,
    double BasalRate,
    double CarbRatio,
    double InsulinSensitivityFactor)
{
    /// <summary>The gap a <see cref="DemoBehaviour.SensorQuiet"/> person's last reading keeps from the end of the range.</summary>
    public static readonly TimeSpan SignalLostLag = TimeSpan.FromMinutes(150);

    /// <summary>Where the daily low of a <see cref="DemoBehaviour.DailyLow"/> person starts, on the local clock.</summary>
    public static readonly TimeSpan DailyLowStart = new(3, 0, 0);

    /// <summary>How long a <see cref="DemoBehaviour.RisingTrend"/> person's glucose climbs.</summary>
    public static readonly TimeSpan RiseCycle = TimeSpan.FromHours(6);

    public const double RiseFloorMgdl = 120;

    public const double RiseCeilingMgdl = 320;

    private const double CorrectedAboveMgdl = 160;

    private const double KeptAboveMgdl = 85;

    private static readonly double[] DailyLowMgdl = [75, 64, 56, 52, 58, 68];

    private static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    /// <summary>Whether the person wears a pump, so has pump changes and a pump mode.</summary>
    public bool HasPump => Therapy != DemoTherapy.Mdi;

    /// <summary>Whether a loop drives the pump, so there is an automatic pump mode, overrides and a loop status.</summary>
    public bool RunsLoop => Therapy == DemoTherapy.Loop;

    /// <summary>The simulation of <paramref name="days"/> days this person's seed runs.</summary>
    public DemoModeConfiguration ConfigurationFor(int days) => new()
    {
        BackfillDays = days,
        RandomSeed = Seed,
        Archetype = this,
        BasalRate = BasalRate,
        CarbRatio = CarbRatio,
        InsulinSensitivityFactor = InsulinSensitivityFactor,
    };

    /// <summary>
    /// What the loop is doing at <paramref name="localTime"/> of a range that ends at <paramref name="end"/> (local):
    /// the sensor gone quiet, or for a looping person the loop's own interruptions, or no loop at all.
    /// </summary>
    public DemoLoopState StateAt(DateTime localTime, DateTime end)
    {
        if (Behaviour.HasFlag(DemoBehaviour.SensorQuiet) && localTime >= end - SignalLostLag)
            return DemoLoopState.SignalLost;

        return RunsLoop ? DemoLoopInterruptions.StateAt(localTime, end, this) : DemoLoopState.NoLoop;
    }

    /// <summary>
    /// The glucose at <paramref name="localTime"/> once the behaviour has shaped the simulated
    /// <paramref name="glucose"/>. A function of the local clock alone, so a continued timeline keeps it true.
    /// </summary>
    public double Shape(DateTime localTime, double glucose)
    {
        if (Behaviour.HasFlag(DemoBehaviour.RisingTrend))
        {
            return RiseFloorMgdl
                + (RiseCeilingMgdl - RiseFloorMgdl) * (localTime.TimeOfDay.TotalMinutes % RiseCycle.TotalMinutes) / RiseCycle.TotalMinutes;
        }

        if (Behaviour.HasFlag(DemoBehaviour.InRange))
        {
            glucose = glucose > CorrectedAboveMgdl
                ? CorrectedAboveMgdl + (glucose - CorrectedAboveMgdl) * 0.85
                : glucose < KeptAboveMgdl ? KeptAboveMgdl - (KeptAboveMgdl - glucose) * 0.4 : glucose;
        }

        return Behaviour.HasFlag(DemoBehaviour.DailyLow) ? Math.Min(glucose, DailyLowAt(localTime) ?? glucose) : glucose;
    }

    private static double? DailyLowAt(DateTime localTime)
    {
        var index = (int)Math.Floor((localTime.TimeOfDay - DailyLowStart) / Step);
        return index >= 0 && index < DailyLowMgdl.Length ? DailyLowMgdl[index] : null;
    }
}

/// <summary>The archetype of each person of the demo clinic, one row each.</summary>
public static class DemoArchetypes
{
    private static readonly (DayScenario, int)[] SteadyDays =
        [(DayScenario.Normal, 70), (DayScenario.Exercise, 20), (DayScenario.StressDay, 10)];

    private static readonly (DayScenario, int)[] HighDays =
        [(DayScenario.HighDay, 55), (DayScenario.StressDay, 20), (DayScenario.SickDay, 15), (DayScenario.Normal, 10)];

    public static readonly IReadOnlyList<DemoArchetype> All =
    [
        new("john", 11, SteadyDays, DemoTherapy.Loop, DemoBehaviour.InRange, 1.0, 10, 40),
        new("katie", 12, SteadyDays, DemoTherapy.Pump, DemoBehaviour.InRange | DemoBehaviour.SensorQuiet, 0.9, 11, 45),
        new("lena", 13, SteadyDays, DemoTherapy.Loop, DemoBehaviour.InRange | DemoBehaviour.DailyLow, 0.55, 15, 70),
        new("tom", 14, HighDays, DemoTherapy.Pump, DemoBehaviour.None, 1.1, 9, 35),
        new("rory", 15, SteadyDays, DemoTherapy.Mdi, DemoBehaviour.RisingTrend, 0.6, 14, 65),
        new("paula", 16, SteadyDays, DemoTherapy.Pump, DemoBehaviour.InRange, 0.95, 10, 42),
        new("liam", 17, SteadyDays, DemoTherapy.Loop, DemoBehaviour.InRange, 0.5, 16, 75),
        new("maddie", 18, SteadyDays, DemoTherapy.Mdi, DemoBehaviour.InRange, 0.7, 13, 60),
        new("sam", 19, SteadyDays, DemoTherapy.Loop, DemoBehaviour.InRange, 0.8, 12, 50),
        new("gus", 20, SteadyDays, DemoTherapy.Loop, DemoBehaviour.InRange, 1.2, 9, 38),
    ];

    /// <summary>The archetype of the person with <paramref name="key"/>; null for a key the clinic does not hold.</summary>
    public static DemoArchetype? Find(string key) => All.FirstOrDefault(a => a.Key == key);
}
