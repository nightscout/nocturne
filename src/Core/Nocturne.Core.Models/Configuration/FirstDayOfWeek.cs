namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// The day a week starts on for a user's regional format, so a weekly bucket on the server
/// begins where the same week does on the user's calendar. A fixed table rather than
/// <c>CultureInfo.DateTimeFormat.FirstDayOfWeek</c>, which Windows NLS and ICU answer differently.
/// <c>FirstDayOfWeekParityTests</c> holds it to what the frontend's Intl lookup returns for every
/// region and language the app offers.
/// </summary>
public static class FirstDayOfWeek
{
    private static readonly HashSet<string> SundayRegions = new(StringComparer.Ordinal)
    {
        "en-US", "en-CA", "en-ZA", "pt-PT", "pt-BR", "ja-JP",
    };

    private static readonly HashSet<string> SundayLanguages = new(StringComparer.Ordinal)
    {
        "en", "pt", "ja", "ko",
    };

    /// <summary>
    /// Sunday or Monday for <paramref name="regionFormat"/>, or for <paramref name="displayLanguage"/>
    /// when the region is empty. Anything else, including no preference, is Monday.
    /// </summary>
    public static DayOfWeek Resolve(string? regionFormat, string? displayLanguage)
    {
        var tag = string.IsNullOrEmpty(regionFormat) ? displayLanguage : regionFormat;
        if (string.IsNullOrEmpty(tag))
        {
            return DayOfWeek.Monday;
        }

        var sunday = string.IsNullOrEmpty(regionFormat)
            ? SundayLanguages.Contains(tag)
            : SundayRegions.Contains(tag);
        return sunday ? DayOfWeek.Sunday : DayOfWeek.Monday;
    }
}
