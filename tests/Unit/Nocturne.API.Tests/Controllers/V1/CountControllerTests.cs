using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Unit tests for CountController verifying IEntryStore integration.
/// </summary>
[Trait("Category", "Unit")]
public class CountControllerTests
{
    private readonly Mock<IEntryStore> _mockEntryStore;
    private readonly Mock<ITreatmentStore> _mockTreatmentStore;
    private readonly Mock<IApsSnapshotRepository> _mockApsSnapshotRepository;
    private readonly Mock<IProfileProjectionService> _mockProfileProjectionService;
    private readonly Mock<IFoodRepository> _mockFoodRepository;
    private readonly Mock<IActivityService> _mockActivityService;
    private readonly Mock<ILogger<CountController>> _mockLogger;
    private readonly CountController _controller;

    public CountControllerTests()
    {
        _mockEntryStore = new Mock<IEntryStore>();
        _mockTreatmentStore = new Mock<ITreatmentStore>();
        _mockApsSnapshotRepository = new Mock<IApsSnapshotRepository>();
        _mockProfileProjectionService = new Mock<IProfileProjectionService>();
        _mockFoodRepository = new Mock<IFoodRepository>();
        _mockActivityService = new Mock<IActivityService>();
        _mockLogger = new Mock<ILogger<CountController>>();

        _controller = new CountController(
            _mockEntryStore.Object,
            _mockTreatmentStore.Object,
            _mockApsSnapshotRepository.Object,
            _mockProfileProjectionService.Object,
            _mockFoodRepository.Object,
            _mockActivityService.Object,
            _mockLogger.Object
        );

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
    }

    [Fact]
    public async Task CountEntries_DelegatesToEntryStore()
    {
        // Arrange
        var find = "{\"type\":\"sgv\"}";
        var type = "sgv";
        _mockEntryStore
            .Setup(s => s.CountAsync(find, type, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42L);

        // Act
        var result = await _controller.CountEntries(find, type);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CountResponse>().Subject;
        response.Count.Should().Be(42L);

        _mockEntryStore.Verify(
            s => s.CountAsync(find, type, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task CountGeneric_Entries_DelegatesToEntryStore()
    {
        // Arrange
        var find = "{\"dateString\":{\"$gte\":\"2024-01-01\"}}";
        var type = "mbg";
        _mockEntryStore
            .Setup(s => s.CountAsync(find, type, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7L);

        // Act
        var result = await _controller.CountGeneric("entries", find, type);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<CountResponse>().Subject;
        response.Count.Should().Be(7L);

        _mockEntryStore.Verify(
            s => s.CountAsync(find, type, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
