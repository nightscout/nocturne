using BenchmarkDotNet.Attributes;
using Nocturne.API.Performance.Tests.Helpers;
using Nocturne.API.Services.Entries;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.API.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class EntryProjectionBenchmarks
{
    private List<SensorGlucoseEntity> _entities100 = null!;
    private List<SensorGlucoseEntity> _entities1000 = null!;
    private List<SensorGlucoseEntity> _entities10000 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entities100 = SensorGlucoseEntityFactory.Generate(100);
        _entities1000 = SensorGlucoseEntityFactory.Generate(1000);
        _entities10000 = SensorGlucoseEntityFactory.Generate(10000);
    }

    // --- Entity -> Domain (SensorGlucoseMapper.ToDomainModel) ---

    [Benchmark(Description = "Entity->Domain_100")]
    public List<SensorGlucose> EntityToDomain_100()
    {
        return _entities100.Select(SensorGlucoseMapper.ToDomainModel).ToList();
    }

    [Benchmark(Description = "Entity->Domain_1000")]
    public List<SensorGlucose> EntityToDomain_1000()
    {
        return _entities1000.Select(SensorGlucoseMapper.ToDomainModel).ToList();
    }

    [Benchmark(Description = "Entity->Domain_10000")]
    public List<SensorGlucose> EntityToDomain_10000()
    {
        return _entities10000.Select(SensorGlucoseMapper.ToDomainModel).ToList();
    }

    // --- Domain -> Legacy Entry (EntryProjection.FromSensorGlucose) ---

    [Benchmark(Description = "Domain->Entry_100")]
    public List<Entry> DomainToEntry_100()
    {
        return _entities100
            .Select(SensorGlucoseMapper.ToDomainModel)
            .Select(EntryProjection.FromSensorGlucose)
            .ToList();
    }

    [Benchmark(Description = "Domain->Entry_1000")]
    public List<Entry> DomainToEntry_1000()
    {
        return _entities1000
            .Select(SensorGlucoseMapper.ToDomainModel)
            .Select(EntryProjection.FromSensorGlucose)
            .ToList();
    }

    [Benchmark(Description = "Domain->Entry_10000")]
    public List<Entry> DomainToEntry_10000()
    {
        return _entities10000
            .Select(SensorGlucoseMapper.ToDomainModel)
            .Select(EntryProjection.FromSensorGlucose)
            .ToList();
    }
}
