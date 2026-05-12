using BenchmarkDotNet.Running;
using Nocturne.API.Performance.Tests.Benchmarks;
using Xunit;

namespace Nocturne.API.Performance.Tests;

public class StatisticsServiceBenchmarkRunner
{
    [Fact]
    [Trait("Category", "Performance")]
    public void RunStatisticsServiceBenchmarks()
    {
        BenchmarkRunner.Run<StatisticsServiceBenchmarks>();
    }
}
