using BenchmarkDotNet.Running;
using Nocturne.API.Performance.Tests.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(StatisticsServiceBenchmarks).Assembly).Run(args);
