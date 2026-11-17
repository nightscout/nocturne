using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class TreatmentReadServiceCountTests
{
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceCountTests()
    {
        _service = new TreatmentReadService(
            _projection.Object,
            Mock.Of<ITreatmentDecomposer>(),
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);
    }

    [Fact]
    public async Task CountAsync_NoFilter_CountsEveryServedTable()
    {
        _projection
            .Setup(p => p.CountProjectedTreatmentsAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(39);

        var result = await _service.CountAsync();

        result.Should().Be(39);
    }

    [Fact]
    public async Task CountAsync_WithTimeRange_PassesTheParsedBounds()
    {
        var find = "{\"created_at\":{\"$gte\":1000,\"$lte\":2000}}";
        _projection
            .Setup(p => p.CountProjectedTreatmentsAsync(1000, 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var result = await _service.CountAsync(find);

        result.Should().Be(5);
    }
}
