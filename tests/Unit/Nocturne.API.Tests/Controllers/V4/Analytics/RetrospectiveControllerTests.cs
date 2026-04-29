using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

[Trait("Category", "Unit")]
public class RetrospectiveControllerTests
{
    private readonly Mock<IIobCalculator> _iobCalculatorMock = new();
    private readonly Mock<ICobCalculator> _cobCalculatorMock = new();
    private readonly Mock<IEntryService> _entryServiceMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepoMock = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepoMock = new();
    private readonly Mock<IBasalRateResolver> _basalRateResolverMock = new();
    private readonly Mock<ILogger<RetrospectiveController>> _loggerMock = new();

    private static DeviceStatusProjectionService CreateProjectionService()
    {
        return new DeviceStatusProjectionService(
            new Mock<IApsSnapshotRepository>().Object,
            new Mock<IPumpSnapshotRepository>().Object,
            new Mock<IUploaderSnapshotRepository>().Object,
            new Mock<IStateSpanRepository>().Object,
            new Mock<IDeviceStatusExtrasRepository>().Object,
            new Mock<ILogger<DeviceStatusProjectionService>>().Object);
    }

    private RetrospectiveController CreateController()
    {
        var controller = new RetrospectiveController(
            _iobCalculatorMock.Object,
            _cobCalculatorMock.Object,
            _entryServiceMock.Object,
            _bolusRepoMock.Object,
            _carbIntakeRepoMock.Object,
            _tempBasalRepoMock.Object,
            CreateProjectionService(),
            _basalRateResolverMock.Object,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    [Fact]
    public async Task GetRetrospectiveData_CallsEntryServiceWithAdvancedFilter()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _entryServiceMock
            .Setup(s => s.GetEntriesWithAdvancedFilterAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entry>());

        _bolusRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<BolusKind?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Bolus>());

        _tempBasalRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>());

        _carbIntakeRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CarbIntake>());

        _iobCalculatorMock
            .Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<Bolus>>(),
                It.IsAny<List<TempBasal>?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult());

        _cobCalculatorMock
            .Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(),
                It.IsAny<List<Bolus>?>(),
                It.IsAny<List<TempBasal>?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult());

        _basalRateResolverMock
            .Setup(s => s.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.8);

        var controller = CreateController();

        // Act
        var result = await controller.GetRetrospectiveData(time);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();

        var fromMills = time - (30 * 60 * 1000);
        var toMills = time + (5 * 60 * 1000);
        var expectedFind = $"{{\"mills\":{{\"$gte\":{fromMills},\"$lte\":{toMills}}}}}";

        _entryServiceMock.Verify(
            s => s.GetEntriesWithAdvancedFilterAsync(
                "sgv",
                50,
                0,
                expectedFind,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
