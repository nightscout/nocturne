namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// The units food energy is displayed in. Stored energy is always kilojoules; see
/// <see cref="Nocturne.Core.Constants.NutritionConstants"/>.
/// </summary>
public static class EnergyUnits
{
    public const string Kilojoules = "kJ";
    public const string Kilocalories = "kcal";

    internal static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { Kilojoules, Kilocalories };

    /// <summary>
    /// Regions whose food labels lead with kilojoules. Display languages carry no region, so an
    /// empty region format never selects one.
    /// </summary>
    private static readonly HashSet<string> KilojouleRegions = new(StringComparer.Ordinal) { "en-AU", "en-NZ", "en-ZA" };

    /// <summary>The unit to display: the user's explicit choice, else the default for their region format.</summary>
    public static string Resolve(string? energyUnit, string? regionFormat) =>
        energyUnit is not null && Allowed.Contains(energyUnit)
            ? energyUnit
            : regionFormat is not null && KilojouleRegions.Contains(regionFormat)
                ? Kilojoules
                : Kilocalories;
}
