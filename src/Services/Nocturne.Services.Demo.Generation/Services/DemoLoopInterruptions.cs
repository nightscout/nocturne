namespace Nocturne.Services.Demo.Services;

/// <summary>What the sample loop is doing at a moment of the history.</summary>
public enum DemoLoopState
{
    /// <summary>Cycling every five minutes and enacting what it asks for.</summary>
    Closed,

    /// <summary>The pump is in manual mode: the loop still cycles and suggests, and enacts nothing.</summary>
    Manual,

    /// <summary>A pod or site change: there is no pump to cycle with.</summary>
    SiteChange,

    /// <summary>A new sensor warming up: there is no glucose, so the loop cannot cycle.</summary>
    SensorWarmUp,

    /// <summary>The phone is out of range of the pump, so no cycle runs or uploads.</summary>
    PhoneOutOfRange,
}

/// <summary>
/// The times the sample loop is not closed. Keyed by date, as <see cref="DayScenarios"/> is, so the
/// device status, the temp basals, the CGM stream and the pump mode spans agree on every seed.
/// </summary>
public static class DemoLoopInterruptions
{
    private static readonly TimeSpan SiteChangeLength = TimeSpan.FromMinutes(30);

    /// <summary>The Dexcom G7 warm-up.</summary>
    private static readonly TimeSpan SensorWarmUpLength = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan ManualModeLength = TimeSpan.FromHours(2);

    /// <summary>
    /// How long before the end of a seeded range no interruption runs, so the tenant looks current
    /// when it is seeded: the loop closed, with a recent status, and its IOB on the dashboard.
    /// </summary>
    public static readonly TimeSpan QuietBeforeEnd = TimeSpan.FromHours(2);

    public sealed record Interruption(DemoLoopState State, DateTime Start, DateTime End);

    /// <summary>
    /// The interruptions that start on <paramref name="localDay"/>, less any that reach into the
    /// <see cref="QuietBeforeEnd"/> before <paramref name="seedEnd"/>. Each ends the same day, so a
    /// day's interruptions are all that can cover a time on it.
    /// </summary>
    /// <param name="seedEnd">The local end of the seeded range, which is now for a live seed.</param>
    public static IReadOnlyList<Interruption> On(DateTime localDay, DateTime seedEnd)
    {
        var day = localDay.Date;
        var interruptions = new List<Interruption>();

        if (DemoDeviceLifecycle.ChangeTimeOn(day, "Site Change") is { } siteChange)
            interruptions.Add(new(DemoLoopState.SiteChange, siteChange, siteChange + SiteChangeLength));
        if (DemoDeviceLifecycle.ChangeTimeOn(day, "Sensor Start") is { } sensorStart)
            interruptions.Add(new(DemoLoopState.SensorWarmUp, sensorStart, sensorStart + SensorWarmUpLength));
        if (ManualModeWindow(day, seedEnd) is { } manual)
            interruptions.Add(new(DemoLoopState.Manual, manual.Start, manual.End));

        AddPhoneGap(interruptions, day, "loop-gap", chancePercent: 75);
        AddPhoneGap(interruptions, day, "loop-gap-2", chancePercent: 45);

        return [.. interruptions.Where(i => !InQuietEnd(i.Start, i.End, seedEnd))];
    }

    /// <summary>
    /// An occasional afternoon in manual mode (pump maintenance, a pool day), on a day with no
    /// workout. The pump mode spans and the loop read the same window.
    /// </summary>
    /// <param name="seedEnd">See <see cref="On"/>.</param>
    public static (DateTime Start, DateTime End)? ManualModeWindow(DateTime localDay, DateTime seedEnd)
    {
        var day = localDay.Date;
        if (DayScenarios.For(day) == DayScenario.Exercise || DayScenarios.Roll(day, "manual-mode", 100) >= 9)
            return null;

        var start = day.AddHours(13 + DayScenarios.Roll(day, "manual-start", 4));
        var end = start + ManualModeLength;
        return InQuietEnd(start, end, seedEnd) ? null : (start, end);
    }

    /// <param name="seedEnd">See <see cref="On"/>.</param>
    public static DemoLoopState StateAt(DateTime localTime, DateTime seedEnd) =>
        On(localTime, seedEnd).FirstOrDefault(i => i.Start <= localTime && localTime < i.End)?.State ?? DemoLoopState.Closed;

    /// <summary>
    /// The minutes from <paramref name="localTime"/> to the next interruption, at most
    /// <paramref name="limit"/>. A temp basal set now ends by then: a site change or manual mode
    /// cancels it, and the loop cannot re-issue it through a gap.
    /// </summary>
    /// <param name="seedEnd">See <see cref="On"/>.</param>
    public static int MinutesToNext(DateTime localTime, int limit, DateTime seedEnd)
    {
        var next = On(localTime, seedEnd).Concat(On(localTime.AddDays(1), seedEnd))
            .Where(i => i.Start > localTime)
            .Select(i => (int)(i.Start - localTime).TotalMinutes)
            .DefaultIfEmpty(limit)
            .Min();
        return Math.Min(next, limit);
    }

    private static bool InQuietEnd(DateTime start, DateTime end, DateTime seedEnd) =>
        start < seedEnd && end > seedEnd - QuietBeforeEnd;

    private static void AddPhoneGap(List<Interruption> interruptions, DateTime day, string stream, int chancePercent)
    {
        if (DayScenarios.Roll(day, stream, 100) >= chancePercent)
            return;

        // Out of the house, from mid-morning to the evening: the phone stays by the pump at home.
        var start = day.AddHours(10.5).AddMinutes(5 * DayScenarios.Roll(day, $"{stream}:start", 126));
        var length = TimeSpan.FromMinutes(20 + 5 * DayScenarios.Roll(day, $"{stream}:length", 11));
        interruptions.Add(new(DemoLoopState.PhoneOutOfRange, start, start + length));
    }
}
