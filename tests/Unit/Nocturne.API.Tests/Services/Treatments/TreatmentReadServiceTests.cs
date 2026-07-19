using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class TreatmentReadServiceTests
{
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<ITreatmentDecomposer> _decomposer = new();
    private readonly Mock<IDecompositionPipeline> _pipeline = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceTests()
    {
        _service = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            _pipeline.Object,
            _tempBasalRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _bolusCalcRepo.Object,
            NullLogger<TreatmentReadService>.Instance);
    }

    [Fact]
    public async Task QueryAsync_DelegatesToProjectionWithNativeOnlyFalse()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 1000 },
            new() { Id = "2", Mills = 2000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, 10, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 10 });

        result.Should().HaveCount(2);
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(null, null, 10, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task QueryAsync_AppliesSkipAndTake()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 3000 },
            new() { Id = "2", Mills = 2000 },
            new() { Id = "3", Mills = 1000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 1, Skip = 1 });

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("2");
    }

    [Fact]
    public async Task QueryAsync_ReverseResults_ReturnsAscendingOrder()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 3000 },
            new() { Id = "2", Mills = 2000 },
            new() { Id = "3", Mills = 1000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, It.IsAny<int>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 10, ReverseResults = true });

        result[0].Mills.Should().Be(1000);
        result[2].Mills.Should().Be(3000);
    }

    [Fact]
    public async Task GetByIdAsync_WithGuid_SearchesV4Repos()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var bolus = new Bolus { Id = id, Timestamp = timestamp };
        var projected = new List<Treatment> { new() { Id = id.ToString(), Mills = 1000 } };

        _bolusRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(1000, 1000, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await _service.GetByIdAsync(id.ToString());

        result.Should().NotBeNull();
        result!.Id.Should().Be(id.ToString());
    }

    [Fact]
    public async Task GetByIdAsync_WithLegacyId_SearchesByLegacyId()
    {
        var legacyId = "abc123";
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var bolus = new Bolus { Id = Guid.NewGuid(), Timestamp = timestamp, LegacyId = legacyId };
        var projected = new List<Treatment> { new() { Id = bolus.Id.ToString(), Mills = 1000 } };

        _bolusRepo.Setup(r => r.GetByLegacyIdAsync(legacyId, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(1000, 1000, 100, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await _service.GetByIdAsync(legacyId);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _bolusRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Bolus?)null);
        _carbIntakeRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CarbIntake?)null);
        _bgCheckRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BGCheck?)null);
        _noteRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Note?)null);
        _deviceEventRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DeviceEvent?)null);
        _bolusCalcRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BolusCalculation?)null);
        _tempBasalRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((TempBasal?)null);

        var result = await _service.GetByIdAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DecomposesEachTreatment()
    {
        var treatment = new Treatment { Id = "t1", Mills = 1000, EventType = "Note", Notes = "test" };
        var decompositionResult = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        decompositionResult.CreatedRecords.Add(new Note
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime
        });

        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decompositionResult);

        var result = await _service.CreateAsync([treatment]);

        result.Should().HaveCount(1);
        _decomposer.Verify(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WhenDecompositionCanceled_PropagatesWithoutTouchingRemaining()
    {
        var canceled = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var next = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };

        _decomposer
            .Setup(d => d.DecomposeAsync(canceled, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _service.CreateAsync([canceled, next]);

        // Cancellation must abort the batch, not be swallowed as a per-record failure.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _decomposer.Verify(
            d => d.DecomposeAsync(next, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenDecompositionFailsForOneRecord_SkipsItAndContinues()
    {
        var bad = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var good = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };

        _decomposer
            .Setup(d => d.DecomposeAsync(bad, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _decomposer
            .Setup(d => d.DecomposeAsync(good, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { CorrelationId = Guid.NewGuid() });

        var result = await _service.CreateAsync([bad, good]);

        // A genuine per-record failure is still isolated: the bad record is dropped,
        // the good one is kept.
        result.Should().ContainSingle().Which.Id.Should().Be("t2");
    }

    [Fact]
    public async Task DeleteAsync_CallsPipelineAndChecksTemp()
    {
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>("t1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _tempBasalRepo
            .Setup(r => r.GetByLegacyIdAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TempBasal?)null);

        var result = await _service.DeleteAsync("t1");

        result.Should().BeTrue();
        _pipeline.Verify(p => p.DeleteByLegacyIdAsync<Treatment>("t1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_ByDerivedObjectId_DeletesViaLegacyIdToRemoveSiblings()
    {
        // A meal bolus + its carb share one LegacyId; deleting by the derived wire ObjectId must
        // route through DeleteByLegacyId (removes both) rather than the single-row range delete
        // (which would orphan the carb into a phantom correction).
        var uuid = Guid.CreateVersion7();
        var wireId = MongoObjectId.FromGuid(uuid);
        var bolus = new Bolus { Id = uuid, LegacyId = "syn-meal-1" };

        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>(wireId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // the ObjectId is not a stored LegacyId
        _bolusRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bolus);
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>("syn-meal-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2); // both siblings

        var result = await _service.DeleteAsync(wireId);

        result.Should().BeTrue();
        _pipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Treatment>("syn-meal-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveCanonicalIdAsync_BackfillsNullLegacyId_SoUpdateUpsertsInPlace()
    {
        // A native V4 row (LegacyId == null) resolved by a derived ObjectId must be backfilled with
        // that ObjectId so the decomposer upserts it in place instead of inserting a duplicate.
        var uuid = Guid.CreateVersion7();
        var wireId = MongoObjectId.FromGuid(uuid);
        var note = new Note { Id = uuid, LegacyId = null };

        _noteRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(note);

        var canonical = await _service.ResolveCanonicalIdAsync(wireId);

        canonical.Should().Be(wireId);
        // The backfill goes through IV4Repository<Note>.UpdateAsync (the base slot the generic
        // helper is typed against), which INoteRepository new-shadows — verify the base slot.
        _noteRepo.As<IV4Repository<Note>>().Verify(
            r => r.UpdateAsync(uuid, It.Is<Note>(n => n.LegacyId == wireId), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
