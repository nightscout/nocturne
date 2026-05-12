using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nocturne.Infrastructure.Data.Performance.Tests.Benchmarks;

// InProcessNoEmitToolchain avoids BenchmarkDotNet's csproj discovery,
// which fails when git worktrees contain duplicate project files.
// WithIterationTime allows long-running DB benchmarks.
var config = DefaultConfig.Instance
    .WithOptions(ConfigOptions.DisableOptimizationsValidator)
    .AddJob(Job.Default
        .WithToolchain(InProcessNoEmitToolchain.Instance));

BenchmarkSwitcher.FromAssembly(typeof(SensorGlucoseQueryBenchmarks).Assembly).Run(args, config);
