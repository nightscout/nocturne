namespace Nocturne.API.Services.Caching;

/// <summary>
/// The record timestamps a write touched, before and after, for clearing only the time-keyed
/// <see cref="ITenantCache"/> entries the write can change. <see cref="ClearsTenantCacheAttribute"/>
/// opens one per request when one of its families is time-keyed; the writer records into it.
/// </summary>
/// <remarks>
/// An empty footprint means the timestamps are unknown, not that there are none, and clears the
/// whole family. A writer that cannot name every timestamp its write moved or removed (a delete by
/// id, say) records nothing.
/// </remarks>
public sealed class TenantCacheWriteFootprint
{
    private static readonly object ItemKey = new();

    private readonly List<DateTime> _timestamps = [];

    public IReadOnlyList<DateTime> Timestamps => _timestamps;

    public void Add(DateTime timestamp) => _timestamps.Add(timestamp);

    public void AddRange(IEnumerable<DateTime> timestamps) => _timestamps.AddRange(timestamps);

    /// <summary>The footprint opened for this request, or <c>null</c> when none was asked for.</summary>
    public static TenantCacheWriteFootprint? Of(HttpContext? http) =>
        http?.Items.TryGetValue(ItemKey, out var footprint) == true ? footprint as TenantCacheWriteFootprint : null;

    internal static TenantCacheWriteFootprint Open(HttpContext http)
    {
        var footprint = new TenantCacheWriteFootprint();
        http.Items[ItemKey] = footprint;
        return footprint;
    }
}
