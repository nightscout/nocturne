using BenchmarkDotNet.Attributes;
using Nocturne.API.Performance.Tests.Helpers;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.API.Performance.Tests.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class TreatmentMappingBenchmarks
{
    private List<BolusEntity> _bolusEntities100 = null!;
    private List<BolusEntity> _bolusEntities1000 = null!;
    private List<BolusEntity> _bolusEntities10000 = null!;

    private List<CarbIntakeEntity> _carbEntities100 = null!;
    private List<CarbIntakeEntity> _carbEntities1000 = null!;
    private List<CarbIntakeEntity> _carbEntities10000 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bolusEntities100 = BolusEntityFactory.Generate(100);
        _bolusEntities1000 = BolusEntityFactory.Generate(1000);
        _bolusEntities10000 = BolusEntityFactory.Generate(10000);

        _carbEntities100 = CarbIntakeEntityFactory.Generate(100);
        _carbEntities1000 = CarbIntakeEntityFactory.Generate(1000);
        _carbEntities10000 = CarbIntakeEntityFactory.Generate(10000);
    }

    // --- BolusMapper.ToDomainModel ---

    [Benchmark(Description = "Bolus_Entity->Domain_100")]
    public List<Bolus> BolusToDomain_100()
    {
        return _bolusEntities100.Select(BolusMapper.ToDomainModel).ToList();
    }

    [Benchmark(Description = "Bolus_Entity->Domain_1000")]
    public List<Bolus> BolusToDomain_1000()
    {
        return _bolusEntities1000.Select(BolusMapper.ToDomainModel).ToList();
    }

    [Benchmark(Description = "Bolus_Entity->Domain_10000")]
    public List<Bolus> BolusToDomain_10000()
    {
        return _bolusEntities10000.Select(BolusMapper.ToDomainModel).ToList();
    }

    // --- CarbIntakeMapper.ToDomainModel ---

    [Benchmark(Description = "CarbIntake_Entity->Domain_100")]
    public List<CarbIntake> CarbIntakeToDomain_100()
    {
        return _carbEntities100.Select(CarbIntakeMapper.ToDomainModel).ToList();
    }

    [Benchmark(Description = "CarbIntake_Entity->Domain_1000")]
    public List<CarbIntake> CarbIntakeToDomain_1000()
    {
        return _carbEntities1000.Select(CarbIntakeMapper.ToDomainModel).ToList();
    }

    [Benchmark(Description = "CarbIntake_Entity->Domain_10000")]
    public List<CarbIntake> CarbIntakeToDomain_10000()
    {
        return _carbEntities10000.Select(CarbIntakeMapper.ToDomainModel).ToList();
    }
}
