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
}
