namespace Nocturne.Core.Constants;

/// <summary>
/// Nutrition unit conversion constants. Food energy is stored in kilojoules.
/// </summary>
public static class NutritionConstants
{
    /// <summary>
    /// Kilojoules in one thermochemical kilocalorie, the factor food labels use.
    /// </summary>
    public const double KilojoulesPerKilocalorie = 4.184;

    /// <summary>
    /// <see cref="KilojoulesPerKilocalorie"/> for decimal arithmetic. A cast of the double is a
    /// different number.
    /// </summary>
    public const decimal KilojoulesPerKilocalorieDecimal = 4.184m;
}
