using System.Text.RegularExpressions;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Guards identifiers that startup DDL interpolates into SQL text. They come from the EF model
/// and from constants, never from user input; the check is belt-and-suspenders so a malformed
/// identifier fails closed (throws) rather than being interpolated.
/// </summary>
internal static class SqlIdentifier
{
    private static readonly Regex Pattern = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Returns <paramref name="identifier"/> when it is a plain snake_case identifier.</summary>
    /// <exception cref="ArgumentException">The identifier is not a plain snake_case name.</exception>
    public static string Require(string identifier, string paramName)
    {
        if (!Pattern.IsMatch(identifier))
            throw new ArgumentException($"Unsafe identifier '{identifier}'.", paramName);
        return identifier;
    }
}
