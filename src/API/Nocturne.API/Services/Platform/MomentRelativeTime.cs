namespace Nocturne.API.Services.Platform;

/// <summary>
/// English relative time as moment.js <c>from</c> words it with default thresholds, the phrasing
/// Nightscout's virtual assistant speech is built on.
/// </summary>
internal static class MomentRelativeTime
{
    public static string From(DateTimeOffset time, DateTimeOffset reference)
    {
        var offset = time - reference;
        var span = offset.Duration();
        var seconds = Round(span.TotalSeconds);
        var minutes = Round(span.TotalMinutes);
        var hours = Round(span.TotalHours);
        var days = Round(span.TotalDays);
        var totalMonths = span.TotalDays * 4800 / 146097;
        var months = Round(totalMonths);
        var years = Round(totalMonths / 12);

        var words = seconds <= 44 ? "a few seconds"
            : minutes <= 1 ? "a minute"
            : minutes < 45 ? $"{minutes} minutes"
            : hours <= 1 ? "an hour"
            : hours < 22 ? $"{hours} hours"
            : days <= 1 ? "a day"
            : days < 26 ? $"{days} days"
            : months <= 1 ? "a month"
            : months < 11 ? $"{months} months"
            : years <= 1 ? "a year"
            : $"{years} years";

        return offset > TimeSpan.Zero ? $"in {words}" : $"{words} ago";
    }

    private static long Round(double value) =>
        (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
