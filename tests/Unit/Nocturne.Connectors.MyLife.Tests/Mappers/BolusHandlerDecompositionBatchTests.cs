using FluentAssertions;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Mappers;

public class BolusHandlerDecompositionBatchTests
{
    private readonly MyLifeEventProcessor _processor = new();

    private static MyLifeEvent CreateBolusEvent(
        double insulin,
        double? carbs = null,
        bool isCalculated = false)
    {
        var infoJson = carbs.HasValue && isCalculated
            ? $"{{\"AmountOfBolus\":{insulin},\"CalcCarbs\":{carbs.Value},\"BolusIsCalculated\":\"true\",\"SuggestedMealBolus\":{insulin}}}"
            : carbs.HasValue
                ? $"{{\"AmountOfBolus\":{insulin},\"CalcCarbs\":{carbs.Value}}}"
                : $"{{\"AmountOfBolus\":{insulin}}}";

        return new MyLifeEvent
        {
            EventTypeId = MyLifeEventType.BolusNormal,
            EventDateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10_000,
            InformationFromDevice = infoJson,
            Value = insulin.ToString(),
            PatientId = "test-patient",
            DeviceId = "test-device",
        };
    }

    [Fact]
    public void MapRecords_BolusWithCarbs_CreatesDecompositionBatch()
    {
        var events = new[] { CreateBolusEvent(insulin: 2.5, carbs: 30) };

        var result = _processor.MapRecords(events, false, false, 0);

        result.DecompositionBatches.Should().HaveCount(1);
        var batch = result.DecompositionBatches[0];
        batch.Source.Should().Be("mylife");
        batch.SourceRecordId.Should().NotBeNullOrEmpty();
        batch.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MapRecords_BolusWithCarbs_CorrelationIdMatchesBatchId()
    {
        var events = new[] { CreateBolusEvent(insulin: 2.5, carbs: 30) };

        var result = _processor.MapRecords(events, false, false, 0);

        result.DecompositionBatches.Should().HaveCount(1);
        var batchId = result.DecompositionBatches[0].Id;

        result.Boluses.Should().HaveCount(1);
        result.Boluses[0].CorrelationId.Should().Be(batchId);

        result.CarbIntakes.Should().HaveCount(1);
        result.CarbIntakes[0].CorrelationId.Should().Be(batchId);
    }

    [Fact]
    public void MapRecords_CalculatedBolus_CreatesDecompositionBatch()
    {
        var events = new[] { CreateBolusEvent(insulin: 3.0, carbs: 45, isCalculated: true) };

        var result = _processor.MapRecords(events, false, false, 0);

        result.DecompositionBatches.Should().HaveCount(1);
        result.BolusCalculations.Should().HaveCount(1);
        result.BolusCalculations[0].CorrelationId.Should().Be(result.DecompositionBatches[0].Id);
    }

    [Fact]
    public void MapRecords_InsulinOnlyBolus_NoDecompositionBatch()
    {
        var events = new[] { CreateBolusEvent(insulin: 2.5) };

        var result = _processor.MapRecords(events, false, false, 0);

        result.DecompositionBatches.Should().BeEmpty();
        result.Boluses.Should().HaveCount(1);
        result.Boluses[0].CorrelationId.Should().BeNull();
    }

    [Fact]
    public void MapRecords_MultipleBoluses_CreatesOneBatchPerCorrelatedBolus()
    {
        var events = new[]
        {
            CreateBolusEvent(insulin: 2.5, carbs: 30),
            CreateBolusEvent(insulin: 1.0),
            CreateBolusEvent(insulin: 3.0, carbs: 50),
        };

        var result = _processor.MapRecords(events, false, false, 0);

        result.DecompositionBatches.Should().HaveCount(2);
        result.DecompositionBatches.Select(b => b.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void MapRecords_DecompositionBatchIds_AreAllDistinct()
    {
        var events = new[]
        {
            CreateBolusEvent(insulin: 1.0, carbs: 10),
            CreateBolusEvent(insulin: 2.0, carbs: 20),
            CreateBolusEvent(insulin: 3.0, carbs: 30),
        };

        var result = _processor.MapRecords(events, false, false, 0);

        result.DecompositionBatches.Should().HaveCount(3);
        var batchIds = result.DecompositionBatches.Select(b => b.Id).ToList();
        var bolusCorrelationIds = result.Boluses.Select(b => b.CorrelationId!.Value).ToList();
        var carbCorrelationIds = result.CarbIntakes.Select(c => c.CorrelationId!.Value).ToList();

        batchIds.Should().OnlyHaveUniqueItems();
        bolusCorrelationIds.Should().BeEquivalentTo(batchIds);
        carbCorrelationIds.Should().BeEquivalentTo(batchIds);
    }
}
