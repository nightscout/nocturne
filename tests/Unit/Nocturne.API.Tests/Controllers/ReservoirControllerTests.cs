using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Devices;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class ReservoirControllerTests
{
    private readonly Mock<IReservoirEstimationService> _estimation = new();
    private readonly ReservoirController _controller;

    public ReservoirControllerTests()
    {
        _controller = new ReservoirController(_estimation.Object);
    }

    [Fact]
    public async Task NoEstimate_ReturnsFoundFalse()
    {
        _estimation
            .Setup(s => s.GetEstimateAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReservoirEstimate?)null);

        var result = await _controller.GetEstimate(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ReservoirEstimateResponse>(ok.Value);
        response.Found.Should().BeFalse();
        response.Units.Should().BeNull();
    }

    [Fact]
    public async Task Estimate_MapsAllFields()
    {
        _estimation
            .Setup(s => s.GetEstimateAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReservoirEstimate(42.5m, IsLowerBound: false, IsEstimated: true));

        var result = await _controller.GetEstimate(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ReservoirEstimateResponse>(ok.Value);
        response.Found.Should().BeTrue();
        response.Units.Should().Be(42.5m);
        response.IsLowerBound.Should().BeFalse();
        response.IsEstimated.Should().BeTrue();
    }

    [Fact]
    public async Task LowerBound_FlagIsCarried()
    {
        _estimation
            .Setup(s => s.GetEstimateAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReservoirEstimate(50m, IsLowerBound: true, IsEstimated: false));

        var result = await _controller.GetEstimate(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ReservoirEstimateResponse>(ok.Value);
        response.Found.Should().BeTrue();
        response.Units.Should().Be(50m);
        response.IsLowerBound.Should().BeTrue();
        response.IsEstimated.Should().BeFalse();
    }
}
