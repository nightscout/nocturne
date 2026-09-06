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
    private readonly CurrentTherapyStateController _controller;

    public CurrentTherapyStateControllerTests()
    {
        // The pump readings are the device category, so the response is redacted without it.
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { Scope.DevicesRead };

        _controller = new CurrentTherapyStateController(
            _stateSpanService.Object,
            _sensitivityResolver.Object,
            _pumpSnapshotRepository.Object)
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
}
