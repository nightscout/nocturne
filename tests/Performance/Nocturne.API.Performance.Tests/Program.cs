using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nocturne.API.Performance.Tests.Benchmarks;

// BenchmarkDotNet's default toolchain rebuilds the whole API graph into a generated project and
// trips its own build timeout, then reports success having executed nothing. Run in-process.
var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance));

BenchmarkSwitcher.FromAssembly(typeof(StatisticsServiceBenchmarks).Assembly).Run(args, config);
