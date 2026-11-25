using System.Globalization;
using System.Net.Mail;

namespace Nocturne.Core.Models.Authorization;

/// <summary>The one rule for when two email addresses are the same address.</summary>
public static class EmailAddresses
{
    /// <summary>
    /// Whether <paramref name="stored"/> and <paramref name="other"/> are the same address: equal
    /// after trimming and ignoring case by ordinal comparison, which folds ASCII and simple
    /// letters only. A compatibility character such as KELVIN SIGN (U+212A) is not its ASCII
    /// letter, so one address never confirms as another. Two missing addresses are not the same,
    /// so setting no address on an account with none still counts as a change.
    /// </summary>
    public static bool AreSame(string? stored, string? other) =>
        stored is not null
        && other is not null
        && string.Equals(stored.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="value"/>, trimmed, is one bare address: <c>local@domain.tld</c> with no
    /// display name, no whitespace, control or format character, and a dotted domain. Anything else
    /// would be stored and mailed as typed yet never match the plain address a person types later
    /// (<see cref="AreSame"/>). A writer stores the trimmed value.
    /// </summary>
    public static bool IsBareAddress(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        if (trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)
            || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return false;

        return MailAddress.TryCreate(trimmed, out var parsed)
            && parsed.DisplayName.Length == 0
            && parsed.Address == trimmed
            && parsed.Host.Contains('.');
    }
}
