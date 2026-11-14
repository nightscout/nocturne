using System.Text.RegularExpressions;

namespace Nocturne.API.Services.Identity;

/// <summary>The format and reserved names a new owner's username must respect.</summary>
public static partial class UsernameRules
{
    public const string FormatMessage =
        "Username must be 3-32 characters: letters, numbers, dots, underscores, and hyphens";

    public const string ReservedMessage = "This username is reserved";

    private static readonly HashSet<string> Reserved = ["admin", "system"];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._\-]{1,30}[a-z0-9]$")]
    private static partial Regex Pattern();

    /// <summary>Lower-cases and trims a username as it is stored.</summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    /// <summary>Why <paramref name="normalized"/> cannot be used, or null when it can.</summary>
    public static string? Refusal(string normalized) =>
        !Pattern().IsMatch(normalized) ? FormatMessage
        : Reserved.Contains(normalized) ? ReservedMessage
        : null;
}
