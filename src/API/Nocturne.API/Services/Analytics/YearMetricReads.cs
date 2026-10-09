using Microsoft.EntityFrameworkCore;

namespace Nocturne.API.Services.Analytics;

internal enum YearMetricSource
{
    Sensor,
    Meter,
    ManualBolus,
    AlgorithmBolus,
    TempBasal,
    Carbs,
}

internal sealed class YearMetricReads
{
    internal TimeZoneInfo? TimeZone { get; set; }
    // Owned by one combined request. Failures are not retained: each report keeps
    // its existing retry and partial-source behavior.
    private readonly Dictionary<(YearMetricSource Source, Type Projection), object> _rows = [];
    private readonly object _gate = new();

    internal async Task<List<T>> ReadAsync<T>(
        IQueryable<T> query,
        YearMetricSource source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (source, typeof(T));
        Task<List<T>> pending;
        lock (_gate)
        {
            if (_rows.TryGetValue(key, out var rows))
                pending = (Task<List<T>>)rows;
            else
            {
                pending = query.ToListAsync(cancellationToken);
                _rows.Add(key, pending);
            }
        }
        try
        {
            return await pending;
        }
        catch
        {
            lock (_gate)
            {
                if (_rows.TryGetValue(key, out var rows) && ReferenceEquals(rows, pending))
                    _rows.Remove(key);
            }
            throw;
        }
    }
}

internal static class YearMetricQueryExtensions
{
    internal static Task<List<T>> ReadMetricRowsAsync<T>(
        this IQueryable<T> query,
        YearMetricReads? reads,
        YearMetricSource source,
        CancellationToken cancellationToken) =>
        reads is null ? query.ToListAsync(cancellationToken) : reads.ReadAsync(query, source, cancellationToken);
}
