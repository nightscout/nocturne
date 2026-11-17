using System.Text;
using System.Text.RegularExpressions;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Text as PostgreSQL can store it. An unpaired UTF-16 surrogate has no UTF-8 form, so Npgsql
/// cannot send it and jsonb refuses its escape; Nightscout's Mongo stores it, so a client may send
/// one. It becomes U+FFFD rather than failing the write on every resend.
/// </summary>
public static class StorableText
{
    /// <summary><paramref name="text"/> with each unpaired surrogate replaced by U+FFFD.</summary>
    public static string ReplaceUnpairedSurrogates(string text) => Replace(text, nul: false);

    /// <summary>As <see cref="ReplaceUnpairedSurrogates"/>, with NUL replaced too, for jsonb.</summary>
    public static string ForJsonb(string text) => Replace(text, nul: true);

    /// <summary>
    /// <paramref name="json"/> with each escape for an unpaired surrogate, and each <c>\u0000</c>,
    /// replaced by the escape for U+FFFD. System.Text.Json cannot read such a string at all, and
    /// jsonb refuses it; Nightscout's JSON.parse takes it.
    /// </summary>
    public static string RepairJsonEscapes(string json) =>
        NulEscape.Replace(
            UnpairedLowEscape.Replace(UnpairedHighEscape.Replace(json, @"$1\ufffd"), @"$1\ufffd"),
            @"$1\ufffd");

    // Each escape counts only after an even number of backslashes: an odd count escapes the backslash.
    private static readonly Regex UnpairedHighEscape = new(
        @"(?<!\\)((?:\\\\)*)\\u(d[89ab][0-9a-f]{2})(?!\\ud[c-f][0-9a-f]{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnpairedLowEscape = new(
        @"(?<!\\ud[89ab][0-9a-f]{2})(?<!\\)((?:\\\\)*)\\u(d[c-f][0-9a-f]{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NulEscape = new(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.Compiled);

    private static string Replace(string text, bool nul)
    {
        StringBuilder? replaced = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var paired = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var bad = (nul && c == '\0') || (char.IsSurrogate(c) && !paired);
            if (bad || replaced is not null)
            {
                replaced ??= new StringBuilder(text, 0, i, text.Length);
                replaced.Append(bad ? '\uFFFD' : c);
                if (paired)
                    replaced.Append(text[++i]);
            }
            else if (paired)
            {
                i++;
            }
        }

        return replaced?.ToString() ?? text;
    }
}
