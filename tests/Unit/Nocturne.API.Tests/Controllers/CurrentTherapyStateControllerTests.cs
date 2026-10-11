using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class CurrentTherapyStateControllerTests
{
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<ISensitivityResolver> _sensitivityResolver = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettingsResolver = new();
    private readonly Mock<ITargetRangeResolver> _targetRangeResolver = new();
    private readonly CurrentTherapyStateController _controller;

    public CurrentTherapyStateControllerTests()
    {
        // The pump readings are the device category, so the response is redacted without it.
        _controller = CreateController(Scope.DevicesRead, Scope.GlucoseRead, Scope.TherapyRead);
    }

    private CurrentTherapyStateController CreateController(params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string>(scopes);

        return new CurrentTherapyStateController(
            _stateSpanService.Object,
            _sensitivityResolver.Object,
            _pumpSnapshotRepository.Object,
            _therapySettingsResolver.Object,
            _targetRangeResolver.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task GetCurrentTherapyState_SurfacesLatestReservoirAndBattery()
    {
        _pumpSnapshotRepository
            .Setup(r => r.GetLatestAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PumpSnapshot
            {
                Reservoir = 87.5,
                BatteryPercent = 64,
                BatteryVoltage = 1.45,
            });

        var result = await _controller.GetCurrentTherapyState();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CurrentTherapyStateResponse>(ok.Value);
        response.Reservoir.Should().Be(87.5);
        response.PumpBatteryPercent.Should().Be(64);
        response.PumpBatteryVoltage.Should().Be(1.45);
    }

    [Fact]
    public async Task GetCurrentTherapyState_NoPumpSnapshot_LeavesPumpFieldsNull()
    {
        _pumpSnapshotRepository
            .Setup(r => r.GetLatestAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PumpSnapshot?)null);

        var result = await _controller.GetCurrentTherapyState();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CurrentTherapyStateResponse>(ok.Value);
        response.Reservoir.Should().BeNull();
        response.PumpBatteryPercent.Should().BeNull();
        response.PumpBatteryVoltage.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentTherapyState_CarriesTheFixedBandAndTheResolvedTargets()
    {
        _therapySettingsResolver
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _targetRangeResolver
            .Setup(r => r.GetLowBGTargetAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(80.0);
        _targetRangeResolver
            .Setup(r => r.GetHighBGTargetAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(160.0);

        var result = await _controller.GetCurrentTherapyState();

        var response = Assert.IsType<CurrentTherapyStateResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.Thresholds.VeryLow.Should().Be(54);
        response.Thresholds.Low.Should().Be(70);
        response.Thresholds.High.Should().Be(180);
        response.Thresholds.VeryHigh.Should().Be(250);
        response.Thresholds.GlucoseYMax.Should().Be(400);
        response.Thresholds.TargetLow.Should().Be(80.0);
        response.Thresholds.TargetHigh.Should().Be(160.0);
    }

    [Fact]
    public async Task GetCurrentTherapyState_NoProfile_HasNoTargets()
    {
        _therapySettingsResolver
            .Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.GetCurrentTherapyState();

        var response = Assert.IsType<CurrentTherapyStateResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.Thresholds.Low.Should().Be(70);
        response.Thresholds.GlucoseYMax.Should().Be(400);
        response.Thresholds.TargetLow.Should().BeNull();
        response.Thresholds.TargetHigh.Should().BeNull();
        _targetRangeResolver.VerifyNoOtherCalls();
    }
}
