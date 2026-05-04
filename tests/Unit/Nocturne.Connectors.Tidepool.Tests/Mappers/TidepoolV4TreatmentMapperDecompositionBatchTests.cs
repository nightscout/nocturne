using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Tidepool.Tests.Mappers;

public class TidepoolV4TreatmentMapperDecompositionBatchTests
{
    private readonly TidepoolV4TreatmentMapper _mapper;

    public TidepoolV4TreatmentMapperDecompositionBatchTests()
    {
        var logger = Mock.Of<ILogger>();
        _mapper = new TidepoolV4TreatmentMapper(logger, "tidepool-connector");
    }

    [Fact]
    public void MapTreatments_BolusAndFoodAtSameTimestamp_CreatesDecompositionBatch()
    {
        var timestamp = DateTime.UtcNow;
        var boluses = new[]
        {
            new TidepoolBolus { Id = "b1", Normal = 3.0, Time = timestamp }
        };
        var foods = new[]
        {
            new TidepoolFood
            {
                Id = "f1",
                Time = timestamp,
                Nutrition = new TidepoolNutrition
                {
                    Carbohydrate = new TidepoolCarbohydrate { Net = 40 }
                }
            }
        };

        var (mappedBoluses, mappedCarbs, batches) = _mapper.MapTreatments(boluses, foods);

        batches.Should().HaveCount(1);
        batches[0].Source.Should().Be("tidepool");
        batches[0].SourceRecordId.Should().Be("f1");
    }

    [Fact]
    public void MapTreatments_BolusAndFoodAtSameTimestamp_CorrelationIdMatchesBatchId()
    {
        var timestamp = DateTime.UtcNow;
        var boluses = new[]
        {
            new TidepoolBolus { Id = "b1", Normal = 3.0, Time = timestamp }
        };
        var foods = new[]
        {
            new TidepoolFood
            {
                Id = "f1",
                Time = timestamp,
                Nutrition = new TidepoolNutrition
                {
                    Carbohydrate = new TidepoolCarbohydrate { Net = 40 }
                }
            }
        };

        var (mappedBoluses, mappedCarbs, batches) = _mapper.MapTreatments(boluses, foods);

        var batchId = batches[0].Id;
        mappedBoluses[0].CorrelationId.Should().Be(batchId);
        mappedCarbs[0].CorrelationId.Should().Be(batchId);
    }

    [Fact]
    public void MapTreatments_StandaloneFood_NoDecompositionBatch()
    {
        var foods = new[]
        {
            new TidepoolFood
            {
                Id = "f1",
                Time = DateTime.UtcNow,
                Nutrition = new TidepoolNutrition
                {
                    Carbohydrate = new TidepoolCarbohydrate { Net = 25 }
                }
            }
        };

        var (_, mappedCarbs, batches) = _mapper.MapTreatments(null, foods);

        batches.Should().BeEmpty();
        mappedCarbs.Should().HaveCount(1);
        mappedCarbs[0].CorrelationId.Should().BeNull();
    }

    [Fact]
    public void MapTreatments_StandaloneBolus_NoDecompositionBatch()
    {
        var boluses = new[]
        {
            new TidepoolBolus { Id = "b1", Normal = 2.0, Time = DateTime.UtcNow }
        };

        var (mappedBoluses, _, batches) = _mapper.MapTreatments(boluses, null);

        batches.Should().BeEmpty();
        mappedBoluses.Should().HaveCount(1);
        mappedBoluses[0].CorrelationId.Should().BeNull();
    }

    [Fact]
    public void MapTreatments_MultipleCorrelatedPairs_CreatesOneBatchPerPair()
    {
        var t1 = DateTime.UtcNow;
        var t2 = t1.AddMinutes(30);
        var boluses = new[]
        {
            new TidepoolBolus { Id = "b1", Normal = 3.0, Time = t1 },
            new TidepoolBolus { Id = "b2", Normal = 2.0, Time = t2 },
        };
        var foods = new[]
        {
            new TidepoolFood
            {
                Id = "f1", Time = t1,
                Nutrition = new TidepoolNutrition { Carbohydrate = new TidepoolCarbohydrate { Net = 40 } }
            },
            new TidepoolFood
            {
                Id = "f2", Time = t2,
                Nutrition = new TidepoolNutrition { Carbohydrate = new TidepoolCarbohydrate { Net = 20 } }
            },
        };

        var (_, _, batches) = _mapper.MapTreatments(boluses, foods);

        batches.Should().HaveCount(2);
        batches.Select(b => b.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void MapTreatments_BolusAndFoodAtDifferentTimestamps_NoDecompositionBatch()
    {
        var boluses = new[]
        {
            new TidepoolBolus { Id = "b1", Normal = 3.0, Time = DateTime.UtcNow }
        };
        var foods = new[]
        {
            new TidepoolFood
            {
                Id = "f1",
                Time = DateTime.UtcNow.AddHours(1),
                Nutrition = new TidepoolNutrition
                {
                    Carbohydrate = new TidepoolCarbohydrate { Net = 40 }
                }
            }
        };

        var (mappedBoluses, mappedCarbs, batches) = _mapper.MapTreatments(boluses, foods);

        batches.Should().BeEmpty();
        mappedBoluses[0].CorrelationId.Should().BeNull();
        mappedCarbs[0].CorrelationId.Should().BeNull();
    }
}
