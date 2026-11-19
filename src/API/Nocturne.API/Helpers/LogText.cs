using System.Globalization;

namespace Nocturne.API.Helpers;

/// <summary>
/// Renders caller-supplied text safe to put in a log line.
/// </summary>
internal static class LogText
{
    /// <summary>Cap on a logged caller-supplied value, which is attacker-controlled free text.</summary>
    private const int MaxLoggedLength = 200;

    /// <summary>
    /// Logs reach a line-oriented console exporter and are shipped verbatim over OTLP, so a control,
    /// format or line-separator character in a header value forges log lines or spoofs how they read.
    /// </summary>
    /// <returns>The value capped and with every unsafe character folded to a space, or <c>(none)</c> when it is empty.</returns>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(none)";

        var capped = value.Length > MaxLoggedLength ? value[..MaxLoggedLength] : value;

        return string.Create(capped.Length, capped, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = IsUnsafe(source[i]) ? ' ' : source[i];
        });
    }

    /// <summary>
    /// Control characters (which include ESC, so ANSI sequences are covered), Unicode format
    /// characters such as the right-to-left override, and the line and paragraph separators.
    /// </summary>
    private static bool IsUnsafe(char value) =>
        char.IsControl(value)
        || char.GetUnicodeCategory(value) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}
