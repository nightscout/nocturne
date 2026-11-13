namespace Nocturne.Core.Contracts.Platform;

/// <summary>
/// When the V4 records behind a legacy collection last changed, as the scope's reader sees them.
/// </summary>
public interface ICollectionLastModifiedReader
{
    /// <summary>
    /// The newest <c>SysUpdatedAt</c> across the tables behind <paramref name="collection"/>, deleted
    /// rows included, or null when they hold nothing.
    /// </summary>
    /// <param name="collection">One of <c>entries</c>, <c>treatments</c>, <c>profile</c> and <c>devicestatus</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="collection"/> is not one of those.</exception>
    Task<DateTime?> LastModifiedAsync(string collection, CancellationToken ct);
}
