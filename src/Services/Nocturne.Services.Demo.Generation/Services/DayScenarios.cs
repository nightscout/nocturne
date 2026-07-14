namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Day-level scenario shaping generated data. One scenario governs a whole
/// calendar day across every data type (glucose, treatments, activity, sleep)
/// so exercise days show high step counts alongside the glucose dip, sick days
/// show elevated readings with low activity, and so on.
/// </summary>
public enum DayScenario
{
    /// <summary>Typical well-managed AID day.</summary>
    Normal,

    /// <summary>Elevated glucose all day (missed boluses, resistant morning).</summary>
    HighDay,

    /// <summary>Prone to lows (over-bolusing, higher sensitivity).</summary>
    LowDay,

    /// <summary>Contains a workout window: glucose dip, step/heart-rate spike.</summary>
    Exercise,

    /// <summary>Illness: elevated glucose and heart rate, minimal activity.</summary>
    SickDay,

    /// <summary>Stress hormones: moderately elevated glucose and heart rate.</summary>
    StressDay,

    /// <summary>Fragmented, inefficient sleep; sluggish low-activity day after.</summary>
    PoorSleep,
}

/// <summary>
/// Deterministic per-date scenario selection shared by every generator. The
/// entry, treatment, activity, and sleep generators each iterate days
/// independently — a per-call random roll would give the same date a different
/// scenario in each stream, so exercise treatments would land on days without
/// the step spike. Hashing the date instead keeps all streams coherent and
/// makes re-seeding idempotent.
/// </summary>
public static class DayScenarios
{
    /// <summary>
    /// Scenario for a calendar date. Weekends skew toward exercise and poor
    /// sleep; weekdays toward normal AID days.
    /// </summary>
    public static DayScenario For(DateTime date)
    {
        var roll = Roll(date, "scenario", 100);
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        if (isWeekend)
        {
            return roll switch
            {
                < 40 => DayScenario.Normal,
                < 55 => DayScenario.HighDay,
                < 70 => DayScenario.Exercise,
                < 80 => DayScenario.PoorSleep,
                < 90 => DayScenario.LowDay,
                < 97 => DayScenario.StressDay,
                _ => DayScenario.SickDay,
            };
        }

        return roll switch
        {
            < 50 => DayScenario.Normal,
            < 65 => DayScenario.HighDay,
            < 78 => DayScenario.LowDay,
            < 88 => DayScenario.Exercise,
            < 94 => DayScenario.StressDay,
            < 98 => DayScenario.PoorSleep,
            _ => DayScenario.SickDay,
        };
    }

    /// <summary>
    /// A <see cref="Random"/> seeded from the date and a stream label, so each
    /// generator gets stable-but-independent noise for the same day.
    /// </summary>
    public static Random RngFor(DateTime date, string stream) =>
        new(unchecked((int)Hash(date, stream)));

    /// <summary>Uniform roll in [0, max) derived from the date and stream label.</summary>
    public static int Roll(DateTime date, string stream, int max) =>
        (int)(Hash(date, stream) % (uint)max);

    /// <summary>
    /// FNV-1a over the day number and stream label. string.GetHashCode is
    /// randomized per process, which would break cross-run determinism.
    /// </summary>
    private static uint Hash(DateTime date, string stream)
    {
        var dayNumber = (int)(date.Date.Ticks / TimeSpan.TicksPerDay);
        var hash = 2166136261u;
        unchecked
        {
            for (var i = 0; i < 4; i++)
            {
                hash ^= (byte)(dayNumber >> (i * 8));
                hash *= 16777619u;
            }
            foreach (var c in stream)
            {
                hash ^= (byte)c;
                hash *= 16777619u;
                hash ^= (byte)(c >> 8);
                hash *= 16777619u;
            }
        }
        return hash;
    }
}
