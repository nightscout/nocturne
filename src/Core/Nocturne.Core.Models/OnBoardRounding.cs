namespace Nocturne.Core.Models;

/// <summary>
/// How insulin and carbs on board are rounded for a summary: insulin to two places, carbs to whole
/// grams, a midpoint to the even neighbour.
/// </summary>
public static class OnBoardRounding
{
    public static double Iob(double units) => ScaledRounding.Round(units, 2);

    public static double Cob(double grams) => ScaledRounding.Round(grams, 0);
}
