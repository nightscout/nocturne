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
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(decompositionResult);

        var result = await _service.CreateAsync([treatment]);

        result.Should().HaveCount(1);
        _decomposer.Verify(d => d.DecomposeAsync(treatment, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_CallsPipelineAndChecksTemp()
    {
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _tempBasalRepo
            .Setup(r => r.GetByLegacyIdAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TempBasal?)null);

        var result = await _service.DeleteAsync("t1");

        result.Should().BeTrue();
        _pipeline.Verify(p => p.DeleteByLegacyIdAsync<Treatment>("t1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
