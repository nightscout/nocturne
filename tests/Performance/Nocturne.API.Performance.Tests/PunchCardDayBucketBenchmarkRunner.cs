using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nocturne.API.Performance.Tests.Benchmarks;
using Xunit;

namespace Nocturne.API.Performance.Tests;

public class PunchCardDayBucketBenchmarkRunner
{
    [Fact]
    [Trait("Category", "Performance")]
    public void RunPunchCardDayBucketBenchmarks()
    {
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));

        BenchmarkRunner.Run<PunchCardDayBucketBenchmarks>(config);
    }
}
