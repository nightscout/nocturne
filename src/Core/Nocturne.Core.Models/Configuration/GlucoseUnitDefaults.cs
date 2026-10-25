namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// The glucose units a person is most likely to read, in the forms
/// <see cref="UserDisplayPreferences.GlucoseUnits"/> stores ("mg/dl" or "mmol").
/// </summary>
public static class GlucoseUnitDefaults
{
    public const string MgDl = "mg/dl";
    public const string Mmol = "mmol";

    /// <summary>
    /// Regions where meters and clinics report glucose in mg/dL. Everywhere else defaults to mmol/L.
    /// Germany, Austria and a few others use both; they are listed by the unit most meters sold
    /// there show. This only picks the pre-selected option: the person always confirms the unit
    /// against an example reading.
    /// </summary>
    private static readonly HashSet<string> MgDlRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "PR", "GU", "VI", "AS", "MP", "UM",
        "MX", "GT", "BZ", "SV", "HN", "NI", "CR", "PA", "CU", "DO", "HT",
        "CO", "VE", "EC", "PE", "BO", "BR", "PY", "UY", "AR", "CL",
        "FR", "BE", "LU", "MC", "DE", "AT", "IT", "SM", "VA", "ES", "AD", "PT", "PL", "GR", "CY", "TR", "GE",
        "IL", "LB", "JO", "SY", "EG", "DZ", "MA", "TN", "LY", "SA", "YE",
        "IN", "BD", "NP", "PK", "LK", "JP", "KR", "TW", "TH", "PH", "ID",
    };

    /// <summary>
    /// The default units for a BCP-47 locale such as "en-AU". A language without a region ("sv",
    /// as browsers often send) stands for the region it is most spoken in. A locale that names no
    /// region at all gets mg/dL, which is what the app showed before anyone chose.
    /// </summary>
    public static string ForLocale(string? locale)
    {
        var region = RegionOf(locale) ?? RegionOf(LikelyLocale(locale));
        if (region is null)
            return MgDl;

        return MgDlRegions.Contains(region) ? MgDl : Mmol;
    }

    /// <summary>
    /// Maps the spellings Nightscout and its uploaders use ("mmol/L", "mmol", "mg/dL", "mgdl") to
    /// "mmol" or "mg/dl". Null for anything else, including null.
    /// </summary>
    public static string? Normalize(string? units)
    {
        if (string.IsNullOrWhiteSpace(units))
            return null;

        var letters = new string(units.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return letters switch
        {
            "mmol" or "mmoll" => Mmol,
            "mgdl" => MgDl,
            _ => null,
        };
    }

    /// <summary>
    /// The specific culture a language-only tag stands for ("sv" is "sv-SE"), or null where the
    /// host has no culture data for it.
    /// </summary>
    private static string? LikelyLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return null;

        try
        {
            return System.Globalization.CultureInfo.CreateSpecificCulture(locale.Trim()).Name;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The two-letter region subtag of a language tag ("zh-Hant-TW" is TW). Read off the tag
    /// rather than through <c>CultureInfo</c>, which throws for every culture on a host without ICU.
    /// </summary>
    private static string? RegionOf(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return null;

        return locale.Trim()
            .Split('-', '_')
            .Skip(1)
            .FirstOrDefault(subtag => subtag.Length == 2 && subtag.All(char.IsAsciiLetter))
            ?.ToUpperInvariant();
    }
}
