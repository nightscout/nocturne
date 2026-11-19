using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V2;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V2;

/// <summary>
/// Unit tests for SummaryController
/// Tests the V2 summary endpoint implementation with 1:1 legacy compatibility
/// </summary>
public class SummaryControllerTests
{
    private readonly Mock<ISummaryService> _mockSummaryService;
    private readonly SummaryController _controller;

    public SummaryControllerTests()
    {
        _mockSummaryService = new Mock<ISummaryService>();
        _controller = new SummaryController(_mockSummaryService.Object);
    }

    [Fact]
    public async Task GetSummary_WithDefaultHours_ReturnsOkResult()
    {
        // Arrange
        var expectedSummary = CreateTestSummaryResponse();
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        // Act
        var result = await _controller.GetSummary(cancellationToken: CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(expectedSummary);

        _mockSummaryService.Verify(
            x => x.GetSummaryAsync(6, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetSummary_WithCustomHours_CallsServiceWithCorrectHours()
    {
        // Arrange
        var expectedSummary = CreateTestSummaryResponse();
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        // Act
        var result = await _controller.GetSummary(12, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        _mockSummaryService.Verify(
            x => x.GetSummaryAsync(12, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetSummary_WithZeroHours_ReturnsBadRequest()
    {
        // Arrange

        // Act
        var result = await _controller.GetSummary(0, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequestResult = result.Result as BadRequestObjectResult;
        badRequestResult!
            .Value.Should()
            .BeEquivalentTo(new { error = "Hours parameter must be greater than 0" });

        _mockSummaryService.Verify(
            x => x.GetSummaryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetSummary_WithNegativeHours_ReturnsBadRequest()
    {
        // Arrange

        // Act
        var result = await _controller.GetSummary(-5, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
        var badRequestResult = result.Result as BadRequestObjectResult;
        badRequestResult!
            .Value.Should()
            .BeEquivalentTo(new { error = "Hours parameter must be greater than 0" });
    }

    [Fact]
    public async Task GetSummary_WhenServiceThrows_TheExceptionEscapes()
    {
        // Arrange
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Test exception"));

        // Act
        var act = () => _controller.GetSummary(cancellationToken: CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("Test exception");
    }

    [Fact]
    public async Task GetSummary_WithValidRequest_ReturnsOkResultWithCorrectData()
    {
        // Arrange
        var expectedSummary = CreateTestSummaryResponse();
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        // Act
        var result = await _controller.GetSummary(cancellationToken: CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        var okResult = result.Result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(expectedSummary);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    public async Task GetSummary_WithVariousValidHours_ReturnsOkResult(int hours)
    {
        // Arrange
        var expectedSummary = CreateTestSummaryResponse();
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(hours, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        // Act
        var result = await _controller.GetSummary(hours, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        _mockSummaryService.Verify(
            x => x.GetSummaryAsync(hours, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetSummary_WithCancellationToken_PassesToService()
    {
        // Arrange
        var expectedSummary = CreateTestSummaryResponse();
        var cancellationToken = new CancellationToken();
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(It.IsAny<int>(), cancellationToken))
            .ReturnsAsync(expectedSummary);

        // Act
        var result = await _controller.GetSummary(6, cancellationToken);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        _mockSummaryService.Verify(x => x.GetSummaryAsync(6, cancellationToken), Times.Once);
    }

    [Fact]
    public async Task GetSummary_WithNullHours_UsesDefaultValue()
    {
        // Arrange
        var expectedSummary = CreateTestSummaryResponse();
        _mockSummaryService
            .Setup(x => x.GetSummaryAsync(6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        // Act
        var result = await _controller.GetSummary(null, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
        _mockSummaryService.Verify(
            x => x.GetSummaryAsync(6, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    private SummaryResponse CreateTestSummaryResponse()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new SummaryResponse
        {
            Sgvs = new List<SummarySgv>
            {
                new SummarySgv { Sgv = 120, Mills = currentTime - 1000 },
                new SummarySgv { Sgv = 115, Mills = currentTime - 300000 },
            },
            Treatments = new SummaryTreatments
            {
                Treatments = new List<SummaryTreatment>
                {
                    new SummaryTreatment
                    {
                        Mills = currentTime - 2000,
                        Insulin = 5.5,
                        Carbs = 30,
                    },
                },
                TempBasals = new List<SummaryTempBasal>
                {
                    new SummaryTempBasal
                    {
                        Start = currentTime - 3600000,
                        Duration = 3600,
                        Absolute = 1.5,
                    },
                },
                Targets = new List<SummaryTarget>
                {
                    new SummaryTarget
                    {
                        Mills = currentTime - 1800000,
                        TargetTop = 180,
                        TargetBottom = 100,
                        Duration = 7200,
                    },
                },
            },
            Profile = new
            {
                defaultProfile = "Default",
                units = "mg/dl",
                basal = new[] { new { time = "00:00", value = 1.0 } },
            },
            State = new SummaryState
            {
                Iob = 2.5,
                Cob = 25,
                Bwp = 1.23,
                Cage = 48,
                Sage = 72,
                Battery = 85,
            },
        };
    }
}
