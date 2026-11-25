namespace Nocturne.Core.Models;

/// <summary>
/// Rounds a <see cref="double"/> to decimal places by scaling it, rounding the product and scaling
/// back, the way JavaScript's <c>Math.round(x * 100) / 100</c> does.
/// </summary>
/// <remarks>
/// <see cref="Math.Round(double, int)"/> worked this way before .NET 11. It now rounds the exact
/// binary value, so 0.175, which is stored as 0.17499999999999998, rounds to 0.17 where this and
/// Nightscout give 0.18.
/// </remarks>
public static class ScaledRounding
{
    private const double ScalingLimit = 1e16;

    private static readonly double[] PowersOfTen =
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
    ];

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="digits"/> is not 0 to 15.</exception>
    public static double Round(double value, int digits, MidpointRounding mode = MidpointRounding.ToEven)
    {
        if ((uint)digits >= (uint)PowersOfTen.Length)
            throw new ArgumentOutOfRangeException(nameof(digits));

        if (Math.Abs(value) >= ScalingLimit)
            return value;

        var scale = PowersOfTen[digits];
        return Math.Round(value * scale, mode) / scale;
    }
}
