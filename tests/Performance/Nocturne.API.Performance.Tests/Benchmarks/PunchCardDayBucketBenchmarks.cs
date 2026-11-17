using BenchmarkDotNet.Attributes;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
public class PunchCardDayBucketBenchmarks
{
    private const int Days = 365;

    private TimeZoneInfo _tz = null!;
    private List<PunchCardDayWindow> _windows = null!;
    private List<SensorGlucose> _glucose = null!;
    private List<Bolus> _boluses = null!;
    private List<CarbIntake> _carbs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        _windows = Enumerable.Range(0, Days).Select(i => PunchCardDayWindow.For(first.AddDays(i), _tz)).ToList();

        var origin = DateTimeOffset.FromUnixTimeMilliseconds(_windows[0].StartMs).UtcDateTime;
        _glucose = Enumerable.Range(0, Days * 1440)
            .Select(i => new SensorGlucose { Timestamp = origin.AddMinutes(i), Mgdl = 70 + i % 150 })
            .ToList();
        _boluses = Enumerable.Range(0, Days * 4)
            .Select(i => new Bolus { Timestamp = origin.AddHours(i * 6), Insulin = 2 })
            .ToList();
        _carbs = Enumerable.Range(0, Days * 3)
            .Select(i => new CarbIntake { Timestamp = origin.AddHours(i * 8), Carbs = 30 })
            .ToList();
    }

    [Benchmark(Baseline = true, Description = "PerDayScan_365Days")]
    public int PerDayScan()
    {
        var total = 0;
        foreach (var w in _windows)
        {
            total += _glucose.Where(e => e.Mills >= w.StartMs && e.Mills <= w.EndMs).ToList().Count;
            total += _boluses.Where(b => b.Mills >= w.StartMs && b.Mills <= w.EndMs).ToList().Count;
            total += _carbs.Where(c => c.Mills >= w.StartMs && c.Mills <= w.EndMs).ToList().Count;
        }
        return total;
    }

    [Benchmark(Description = "Bucketed_365Days")]
    public int Bucketed()
    {
        var glucose = PunchCardDayBuckets.Bucket(_glucose, _windows, e => e.Mills);
        var boluses = PunchCardDayBuckets.Bucket(_boluses, _windows, b => b.Mills);
        var carbs = PunchCardDayBuckets.Bucket(_carbs, _windows, c => c.Mills);
        var total = 0;
        for (var i = 0; i < _windows.Count; i++) total += glucose[i].Count + boluses[i].Count + carbs[i].Count;
        return total;
    }
}
