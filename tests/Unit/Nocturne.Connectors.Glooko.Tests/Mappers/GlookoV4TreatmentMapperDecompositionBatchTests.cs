using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

public class GlookoV4TreatmentMapperDecompositionBatchTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperDecompositionBatchTests()
    {
        var logger = Mock.Of<ILogger>();
        var config = new GlookoConnectorConfiguration();
        var timeMapper = new GlookoTimeMapper(config, logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    #region V2 MapBatchData

    [Fact]
    public void MapBatchData_BolusWithCarbs_CreatesDecompositionBatch()
    {
        var batchData = new GlookoBatchData
        {
            NormalBoluses =
            [
                new GlookoBolus
                {
                    Timestamp = "2026-04-28T10:00:00Z",
                    PumpTimestamp = "2026-04-28T10:00:00Z",
                    InsulinDelivered = 3.0,
                    CarbsInput = 40,
                }
            ]
        };

        var (boluses, carbs, batches) = _mapper.MapBatchData(batchData);

        batches.Should().HaveCount(1);
        batches[0].Source.Should().Be("glooko");
        batches[0].SourceRecordId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void MapBatchData_BolusWithCarbs_CorrelationIdMatchesBatchId()
    {
        var batchData = new GlookoBatchData
        {
            NormalBoluses =
            [
                new GlookoBolus
                {
                    Timestamp = "2026-04-28T10:00:00Z",
                    PumpTimestamp = "2026-04-28T10:00:00Z",
                    InsulinDelivered = 3.0,
                    CarbsInput = 40,
                }
            ]
        };

        var (boluses, carbs, batches) = _mapper.MapBatchData(batchData);

        var batchId = batches[0].Id;
        boluses[0].CorrelationId.Should().Be(batchId);
        carbs[0].CorrelationId.Should().Be(batchId);
    }

    [Fact]
    public void MapBatchData_InsulinOnlyBolus_NoDecompositionBatch()
    {
        var batchData = new GlookoBatchData
        {
            NormalBoluses =
            [
                new GlookoBolus
                {
                    Timestamp = "2026-04-28T10:00:00Z",
                    PumpTimestamp = "2026-04-28T10:00:00Z",
                    InsulinDelivered = 2.0,
                    CarbsInput = 0,
                }
            ]
        };

        var (boluses, _, batches) = _mapper.MapBatchData(batchData);

        batches.Should().BeEmpty();
        boluses[0].CorrelationId.Should().BeNull();
    }

    [Fact]
    public void MapBatchData_MultipleBoluses_CreatesBatchPerCorrelatedBolus()
    {
        var batchData = new GlookoBatchData
        {
            NormalBoluses =
            [
                new GlookoBolus
                {
                    Timestamp = "2026-04-28T10:00:00Z",
                    PumpTimestamp = "2026-04-28T10:00:00Z",
                    InsulinDelivered = 3.0,
                    CarbsInput = 40,
                },
                new GlookoBolus
                {
                    Timestamp = "2026-04-28T11:00:00Z",
                    PumpTimestamp = "2026-04-28T11:00:00Z",
                    InsulinDelivered = 1.0,
                    CarbsInput = 0,
                },
                new GlookoBolus
                {
                    Timestamp = "2026-04-28T12:00:00Z",
                    PumpTimestamp = "2026-04-28T12:00:00Z",
                    InsulinDelivered = 2.5,
                    CarbsInput = 30,
                },
            ]
        };

        var (_, _, batches) = _mapper.MapBatchData(batchData);

        batches.Should().HaveCount(2);
        batches.Select(b => b.Id).Should().OnlyHaveUniqueItems();
    }

    #endregion

    #region V3 MapV3Boluses

    [Fact]
    public void MapV3Boluses_BolusWithCarbs_CreatesDecompositionBatch()
    {
        var graphData = new GlookoV3GraphResponse
        {
            Series = new GlookoV3Series
            {
                DeliveredBolus =
                [
                    new GlookoV3BolusDataPoint
                    {
                        X = new DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                        Y = 3.0,
                        Data = new GlookoV3BolusData
                        {
                            DeliveredUnits = 3.0,
                            CarbsInput = 40,
                        }
                    }
                ]
            }
        };

        var (boluses, carbs, batches) = _mapper.MapV3Boluses(graphData);

        batches.Should().HaveCount(1);
        batches[0].Source.Should().Be("glooko");
    }

    [Fact]
    public void MapV3Boluses_BolusWithCarbs_CorrelationIdMatchesBatchId()
    {
        var graphData = new GlookoV3GraphResponse
        {
            Series = new GlookoV3Series
            {
                DeliveredBolus =
                [
                    new GlookoV3BolusDataPoint
                    {
                        X = new DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                        Y = 3.0,
                        Data = new GlookoV3BolusData
                        {
                            DeliveredUnits = 3.0,
                            CarbsInput = 40,
                        }
                    }
                ]
            }
        };

        var (boluses, carbs, batches) = _mapper.MapV3Boluses(graphData);

        var batchId = batches[0].Id;
        boluses[0].CorrelationId.Should().Be(batchId);
        carbs[0].CorrelationId.Should().Be(batchId);
    }

    [Fact]
    public void MapV3Boluses_InsulinOnlyBolus_NoDecompositionBatch()
    {
        var graphData = new GlookoV3GraphResponse
        {
            Series = new GlookoV3Series
            {
                DeliveredBolus =
                [
                    new GlookoV3BolusDataPoint
                    {
                        X = new DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                        Y = 2.0,
                        Data = new GlookoV3BolusData { DeliveredUnits = 2.0 }
                    }
                ]
            }
        };

        var (boluses, _, batches) = _mapper.MapV3Boluses(graphData);

        batches.Should().BeEmpty();
        boluses[0].CorrelationId.Should().BeNull();
    }

    [Fact]
    public void MapV3Boluses_NullSeries_ReturnsEmptyBatches()
    {
        var graphData = new GlookoV3GraphResponse { Series = null };

        var (_, _, batches) = _mapper.MapV3Boluses(graphData);

        batches.Should().BeEmpty();
    }

    #endregion
}
