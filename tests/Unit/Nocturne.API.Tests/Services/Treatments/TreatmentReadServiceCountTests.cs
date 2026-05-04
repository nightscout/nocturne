using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class TreatmentReadServiceCountTests
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

    public TreatmentReadServiceCountTests()
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
    public async Task CountAsync_NoFilter_SumsAllRepos()
    {
        _bolusRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(10);
        _carbIntakeRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _bgCheckRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        _noteRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _deviceEventRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(4);
        _tempBasalRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(8);
        _bolusCalcRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _service.CountAsync();

        result.Should().Be(33);
    }

    [Fact]
    public async Task CountAsync_WithTimeRange_PassesParsedTimesToRepos()
    {
        var find = "{\"created_at\":{\"$gte\":1000,\"$lte\":2000}}";

        _bolusRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _carbIntakeRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _bgCheckRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _noteRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _deviceEventRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _tempBasalRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _bolusCalcRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await _service.CountAsync(find);

        result.Should().Be(5);
        _bolusRepo.Verify(r => r.CountAsync(
            It.Is<DateTime?>(d => d.HasValue),
            It.Is<DateTime?>(d => d.HasValue),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
