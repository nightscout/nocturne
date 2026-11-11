namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// When a credential or membership presented on a request is stamped as last used.
/// </summary>
/// <remarks>
/// The stamp is a write on its own context, so stamping every request would open a context for each
/// one. Once per <see cref="Interval"/> is as precise as the "Last active" lines that read it need.
/// A repository applies <see cref="StaleBefore"/> in its UPDATE, so writes that race across processes
/// stamp once.
/// </remarks>
public static class LastUsedStamp
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Whether a row last stamped at <paramref name="lastUsedAt"/> is due a new stamp.</summary>
    public static bool IsDue(DateTime? lastUsedAt, DateTime now) =>
        lastUsedAt is not { } last || now - last > Interval;

    /// <summary>The instant a stamp must predate for a stamp at <paramref name="now"/> to be due.</summary>
    public static DateTime StaleBefore(DateTime now) => now - Interval;
}
