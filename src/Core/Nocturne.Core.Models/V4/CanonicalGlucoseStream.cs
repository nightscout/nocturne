namespace Nocturne.Core.Models.V4;

/// <summary>
/// Selects the canonical glucose stream from readings that may span multiple concurrent CGMs.
/// </summary>
/// <remarks>
/// <para>
/// Multiple CGMs worn at once (sensor-overlap warmup, n=1 comparisons) produce overlapping
/// readings that double-count in statistics and jitter single-stream consumers (v1/v3 clients,
/// alarms). The canonical view resolves this at read time: per aligned 5-minute UTC bucket, the
/// highest-priority stream that has at least one reading wins, and <b>all</b> of that stream's
/// readings in the bucket are emitted (preserving 1-minute-cadence density). No data is discarded
/// at rest — selection is a pure projection over what was fetched.
/// </para>
/// <para>
/// A stream is a registered <see cref="PatientDevice"/> when the reading carries
/// <see cref="SensorGlucose.PatientDeviceId"/>, otherwise a pseudo-device keyed by
/// <c>(DataSource, Device)</c>. Registered devices order by <see cref="PatientDevice.Rank"/>
/// ascending (nulls last), then most recent <see cref="PatientDevice.StartDate"/>, then
/// <see cref="PatientDevice.CreatedAt"/> descending. Pseudo-devices rank below every registered
/// device, ordered by key for determinism. A user with a single stream — the overwhelmingly
/// common case — gets their readings back unchanged.
/// </para>
/// </remarks>
/// <seealso cref="PatientDevice"/>
/// <seealso cref="SensorGlucose"/>
public static class CanonicalGlucoseStream
{
    /// <summary>Bucket width for stream selection.</summary>
    public static readonly TimeSpan BucketSize = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Filters <paramref name="readings"/> to the canonical stream. Input order is preserved.
    /// </summary>
    /// <param name="readings">Readings fetched for the caller's window, any order.</param>
    /// <param name="devices">
    /// The patient's registered devices (any category; only CGM entries participate). Order is
    /// irrelevant — priority is computed here.
    /// </param>
    public static IReadOnlyList<SensorGlucose> Select(
        IReadOnlyList<SensorGlucose> readings,
        IReadOnlyList<PatientDevice> devices)
    {
        if (readings.Count == 0)
            return readings;

        // Fast path: one stream present → canonical view is the input itself.
        var firstKey = StreamKey(readings[0]);
        var multiStream = false;
        for (var i = 1; i < readings.Count; i++)
        {
            if (StreamKey(readings[i]) != firstKey) { multiStream = true; break; }
        }
        if (!multiStream)
            return readings;

        var priority = BuildPriorityMap(readings, devices);

        // Per bucket, the present stream with the lowest priority value wins.
        var bucketWinner = new Dictionary<long, (int Priority, string Key)>();
        foreach (var reading in readings)
        {
            var bucket = reading.Timestamp.Ticks / BucketSize.Ticks;
            var key = StreamKey(reading);
            var p = priority[key];
            if (!bucketWinner.TryGetValue(bucket, out var winner) || p < winner.Priority)
                bucketWinner[bucket] = (p, key);
        }

        var result = new List<SensorGlucose>(readings.Count);
        foreach (var reading in readings)
        {
            var bucket = reading.Timestamp.Ticks / BucketSize.Ticks;
            if (bucketWinner[bucket].Key == StreamKey(reading))
                result.Add(reading);
        }
        return result;
    }

    /// <summary>
    /// The stream identity of a reading: the registered device id, or a pseudo-device key from
    /// its source fields.
    /// </summary>
    public static string StreamKey(SensorGlucose reading) =>
        reading.PatientDeviceId is { } id
            ? id.ToString()
            : $"~{reading.DataSource}|{reading.Device}";

    /// <summary>
    /// Priority per stream key present in the readings (lower wins). Registered CGM devices order
    /// by rank/recency; pseudo-devices follow, ordered by key. A reading whose PatientDeviceId is
    /// not in <paramref name="devices"/> (deleted/foreign) is treated as a pseudo-device.
    /// </summary>
    private static Dictionary<string, int> BuildPriorityMap(
        IReadOnlyList<SensorGlucose> readings,
        IReadOnlyList<PatientDevice> devices)
    {
        var rankedDeviceKeys = devices
            .Where(d => d.DeviceCategory == DeviceCategory.CGM)
            .OrderBy(d => d.Rank == null)
            .ThenBy(d => d.Rank)
            .ThenByDescending(d => d.StartDate.HasValue)
            .ThenByDescending(d => d.StartDate)
            .ThenByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Select(d => d.Id.ToString())
            .ToList();

        var priority = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rankedDeviceKeys.Count; i++)
            priority.TryAdd(rankedDeviceKeys[i], i);

        var unknown = readings
            .Select(StreamKey)
            .Where(k => !priority.ContainsKey(k))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);

        var next = rankedDeviceKeys.Count;
        foreach (var key in unknown)
            priority[key] = next++;

        return priority;
    }
}
