using BenchmarkDotNet.Attributes;
using Nocturne.API.Performance.Tests.Helpers;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class StatisticsServiceBenchmarks
{
    private StatisticsService _service = null!;
    private List<SensorGlucose> _dayEntries = null!;    // 288 readings
    private List<SensorGlucose> _weekEntries = null!;   // 2016 readings
    private List<SensorGlucose> _quarterEntries = null!; // 25920 readings (90 days)

    [GlobalSetup]
    public void Setup()
    {
        _service = new StatisticsService();
        _dayEntries = SensorGlucoseFactory.Generate(288);
        _weekEntries = SensorGlucoseFactory.Generate(2016);
        _quarterEntries = SensorGlucoseFactory.Generate(25920);
    }

    // --- CalculateTimeInRange (the heaviest method — multiple .Count() + .Where().ToList()) ---

    [Benchmark(Description = "TimeInRange_1Day")]
    public object TimeInRange_Day() => _service.CalculateTimeInRange(_dayEntries);

    [Benchmark(Description = "TimeInRange_1Week")]
    public object TimeInRange_Week() => _service.CalculateTimeInRange(_weekEntries);

    [Benchmark(Description = "TimeInRange_90Days")]
    public object TimeInRange_Quarter() => _service.CalculateTimeInRange(_quarterEntries);

    // --- CalculateBasicStats (values filter + sort) ---

    [Benchmark(Description = "BasicStats_1Day")]
    public object BasicStats_Day() => _service.CalculateBasicStats(
        _dayEntries.Select(e => e.Mgdl).Where(v => v > 0 && v < 600).ToList());

    [Benchmark(Description = "BasicStats_1Week")]
    public object BasicStats_Week() => _service.CalculateBasicStats(
        _weekEntries.Select(e => e.Mgdl).Where(v => v > 0 && v < 600).ToList());

    [Benchmark(Description = "BasicStats_90Days")]
    public object BasicStats_Quarter() => _service.CalculateBasicStats(
        _quarterEntries.Select(e => e.Mgdl).Where(v => v > 0 && v < 600).ToList());

    // --- CalculateGlucoseDistribution (filter + bin counting) ---

    [Benchmark(Description = "Distribution_1Day")]
    public object Distribution_Day() => _service.CalculateGlucoseDistribution(_dayEntries);

    [Benchmark(Description = "Distribution_1Week")]
    public object Distribution_Week() => _service.CalculateGlucoseDistribution(_weekEntries);

    [Benchmark(Description = "Distribution_90Days")]
    public object Distribution_Quarter() => _service.CalculateGlucoseDistribution(_quarterEntries);

    // --- CalculateAveragedStats (24-bucket grouping + per-bucket stats) ---

    [Benchmark(Description = "AveragedStats_1Day")]
    public object AveragedStats_Day() => _service.CalculateAveragedStats(_dayEntries);

    [Benchmark(Description = "AveragedStats_1Week")]
    public object AveragedStats_Week() => _service.CalculateAveragedStats(_weekEntries);

    [Benchmark(Description = "AveragedStats_90Days")]
    public object AveragedStats_Quarter() => _service.CalculateAveragedStats(_quarterEntries);
}
