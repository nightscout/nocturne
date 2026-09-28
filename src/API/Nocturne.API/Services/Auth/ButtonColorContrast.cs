using System.Globalization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Picks legible text for a button filled with an admin-configured brand colour. The
/// colour is free text, so only hex forms are understood; anything else yields no
/// foreground and the login page keeps its themed outline button.
/// </summary>
public static class ButtonColorContrast
{
    public const string Light = "#ffffff";
    public const string Dark = "#0a0a0a";

    /// <summary>
    /// <see cref="Light"/> or <see cref="Dark"/>, whichever has the higher WCAG 2 contrast
    /// ratio against <paramref name="background"/>; null when it is not <c>#rgb</c>,
    /// <c>#rgba</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.
    /// </summary>
    public static string? ForegroundFor(string? background)
    {
        if (!TryParseHex(background, out var r, out var g, out var b))
            return null;

        var luminance = Luminance(r, g, b);
        var againstLight = (Luminance(0xff, 0xff, 0xff) + 0.05) / (luminance + 0.05);
        var againstDark = (luminance + 0.05) / (Luminance(0x0a, 0x0a, 0x0a) + 0.05);
        return againstLight >= againstDark ? Light : Dark;
    }

    private static double Luminance(int r, int g, int b) =>
        0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static bool TryParseHex(string? value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var hex = value?.Trim();
        if (hex is null || !hex.StartsWith('#'))
            return false;
        hex = hex[1..];

        if (hex.Length is 3 or 4)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        else if (hex.Length is 8)
            hex = hex[..6];
        else if (hex.Length is not 6)
            return false;

        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        r = (rgb >> 16) & 0xff;
        g = (rgb >> 8) & 0xff;
        b = rgb & 0xff;
        return true;
    }
}
