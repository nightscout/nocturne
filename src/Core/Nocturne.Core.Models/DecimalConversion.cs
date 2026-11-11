using System.Globalization;

namespace Nocturne.Core.Models;

/// <summary>
/// Converts a binary floating-point value to the <see cref="decimal"/> it prints as.
/// </summary>
/// <remarks>
/// From .NET 11 a cast keeps the exact binary value: <c>(decimal)1.2</c> is
/// <c>1.1999999999999999555910790150</c>, which is stored with that tail, is not equal to
/// <c>1.2m</c> and falls below a threshold of 1.2. These round to the 15 (<see cref="double"/>) or
/// 7 (<see cref="float"/>) significant digits a cast kept before .NET 11. The compiler folds a cast
/// of a <see langword="const"/> the same way, so a decimal factor is declared as a decimal constant
/// rather than cast from its double.
/// </remarks>
public static class DecimalConversion
{
    /// <exception cref="OverflowException">The value is not finite or is outside the decimal range.</exception>
    public static decimal ToDecimal(this double value)
    {
        if (!double.IsFinite(value))
            throw new OverflowException("Value was either too large or too small for a Decimal.");

        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out var length, "G15", CultureInfo.InvariantCulture);
        return decimal.Parse(text[..length], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc cref="ToDecimal(double)"/>
    public static decimal ToDecimal(this float value)
    {
        if (!float.IsFinite(value))
            throw new OverflowException("Value was either too large or too small for a Decimal.");

        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out var length, "G7", CultureInfo.InvariantCulture);
        return decimal.Parse(text[..length], NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
