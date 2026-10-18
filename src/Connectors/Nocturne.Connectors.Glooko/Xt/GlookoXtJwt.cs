using System.Text.Json;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     Reads the expiry out of the JWT Glooko XT issues, without verifying it — the server is the
///     only party that can, and the connector merely needs to know when to send the tenant back
///     through the sign-in rather than fail every sync with a token that has lapsed.
/// </summary>
public static class GlookoXtJwt
{
    /// <summary>The <c>exp</c> claim as UTC, or null when the token carries none or is not a JWT.</summary>
    public static DateTime? TryGetExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return null;

            var seconds = exp.ValueKind switch
            {
                JsonValueKind.Number when exp.TryGetInt64(out var l) => l,
                JsonValueKind.Number => (long)exp.GetDouble(),
                JsonValueKind.String when long.TryParse(exp.GetString(), out var s) => s,
                _ => (long?)null,
            };

            return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    /// <summary>Whether <paramref name="token"/> looks like the three-part JWT the code exchange returns.</summary>
    public static bool LooksLikeJwt(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Count(c => c == '.') == 2 && !token.Any(char.IsWhiteSpace);
}
