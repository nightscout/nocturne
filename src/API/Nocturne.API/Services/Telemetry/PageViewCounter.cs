using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.Core.Contracts.Telemetry;

namespace Nocturne.API.Services.Telemetry;

/// <summary>
/// Page views counted in memory, by UTC day and route id, until
/// <see cref="PageViewCountFlusher"/> adds them to <see cref="IPageViewCountStore"/>.
/// </summary>
/// <remarks>
/// Only ids in <see cref="PageRouteIds.All"/> are counted, so the counts held here are bounded by
/// that list times the days since the last flush, whether or not the database is reachable.
/// </remarks>
public sealed class PageViewCounter(IOptions<PageCountsOptions> options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private Dictionary<(DateOnly Date, string RouteId), long> _pending = [];

    public bool Enabled => options.Value.Enabled;

    /// <summary>
    /// Counts one view of <paramref name="routeId"/> today. Does nothing while page counts are off
    /// or when the id is not a page of the app.
    /// </summary>
    /// <returns>Whether the view was counted.</returns>
    public bool Record(string routeId)
    {
        if (!Enabled || !PageRouteIds.All.TryGetValue(routeId, out var known))
            return false;

        var key = (DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), known);
        lock (_gate)
            _pending[key] = _pending.GetValueOrDefault(key) + 1;
        return true;
    }

    /// <summary>Takes every count held so far, leaving none.</summary>
    public IReadOnlyCollection<PageViewCount> Drain()
    {
        Dictionary<(DateOnly Date, string RouteId), long> drained;
        lock (_gate)
        {
            drained = _pending;
            _pending = [];
        }

        return drained.Select(p => new PageViewCount(p.Key.Date, p.Key.RouteId, p.Value)).ToList();
    }

    /// <summary>Puts back counts a flush could not store, adding them to any counted since.</summary>
    public void Restore(IEnumerable<PageViewCount> counts)
    {
        lock (_gate)
        {
            foreach (var count in counts)
            {
                var key = (count.Date, count.RouteId);
                _pending[key] = _pending.GetValueOrDefault(key) + count.Count;
            }
        }
    }
}
