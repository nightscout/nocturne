using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Unit tests for TreatmentService domain service with WebSocket broadcasting
/// </summary>
[Parity("api.treatments.test.js")]
public class TreatmentServiceTests
{
    private readonly Mock<IPostgreSqlService> _mockPostgreSqlService;
    private readonly Mock<ISignalRBroadcastService> _mockBroadcastService;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<IOptions<CacheConfiguration>> _mockCacheConfig;
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<IStateSpanService> _mockStateSpanService;
    private readonly Mock<ILogger<TreatmentService>> _mockLogger;
    private readonly TreatmentService _treatmentService;

    public TreatmentServiceTests()
    {
        _mockPostgreSqlService = new Mock<IPostgreSqlService>();
        _mockBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockCacheService = new Mock<ICacheService>();
        _mockCacheConfig = new Mock<IOptions<CacheConfiguration>>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockStateSpanService = new Mock<IStateSpanService>();
        _mockLogger = new Mock<ILogger<TreatmentService>>();

        _mockCacheConfig.Setup(x => x.Value).Returns(new CacheConfiguration());
        _mockDemoModeService.Setup(x => x.IsEnabled).Returns(false);
        _mockStateSpanService
            .Setup(x => x.GetTempBasalsAsTreatmentsAsync(
                It.IsAny<long?>(),
                It.IsAny<long?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment>());

        _treatmentService = new TreatmentService(
            _mockPostgreSqlService.Object,
            _mockBroadcastService.Object,
            _mockCacheService.Object,
            _mockCacheConfig.Object,
            _mockDemoModeService.Object,
            _mockStateSpanService.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    public async Task GetTreatmentsAsync_ShouldCallMongoDbService()
    {
        // Arrange
        var expectedTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "1",
                EventType = "Meal Bolus",
                Insulin = 5.0,
                Carbs = 50,
            },
            new Treatment
            {
                Id = "2",
                EventType = "Correction Bolus",
                Insulin = 2.0,
            },
        };

        // Production code now uses GetTreatmentsWithAdvancedFilterAsync with demo mode filter
        _mockPostgreSqlService
            .Setup(x =>
                x.GetTreatmentsWithAdvancedFilterAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedTreatments);

        // Setup cache service to execute the factory function and return the result
        _mockCacheService
            .Setup(x =>
                x.GetOrSetAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<List<Treatment>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<string, Func<Task<List<Treatment>>>, TimeSpan, CancellationToken>(
                async (key, factory, ttl, ct) => await factory()
            );

        // Act
        var result = await _treatmentService.GetTreatmentsAsync(
            count: 10,
            skip: 0,
            cancellationToken: CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetTreatmentsWithAdvancedFilterAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldBroadcastStorageCreateEvents()
    {
        // Arrange
        var treatmentsToCreate = new List<Treatment>
        {
            new Treatment
            {
                EventType = "Meal Bolus",
                Insulin = 5.0,
                Carbs = 50,
            },
            new Treatment { EventType = "Correction Bolus", Insulin = 2.0 },
        };
        var createdTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "1",
                EventType = "Meal Bolus",
                Insulin = 5.0,
                Carbs = 50,
            },
            new Treatment
            {
                Id = "2",
                EventType = "Correction Bolus",
                Insulin = 2.0,
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.CreateTreatmentsAsync(treatmentsToCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdTreatments);

        // Act
        var result = await _treatmentService.CreateTreatmentsAsync(
            treatmentsToCreate,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);

        // Verify MongoDB service was called
        _mockPostgreSqlService.Verify(
            x => x.CreateTreatmentsAsync(treatmentsToCreate, It.IsAny<CancellationToken>()),
            Times.Once
        ); // Verify WebSocket broadcasting was called for each treatment
        _mockBroadcastService.Verify(
            x =>
                x.BroadcastStorageCreateAsync(
                    "treatments",
                    It.Is<object>(o => o.ToString()!.Contains("treatments"))
                ),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdateTreatmentAsync_ShouldBroadcastStorageUpdateEvent()
    {
        // Arrange
        var treatmentId = "test-id";
        var treatmentToUpdate = new Treatment
        {
            EventType = "Meal Bolus",
            Insulin = 5.0,
            Carbs = 50,
        };
        var updatedTreatment = new Treatment
        {
            Id = treatmentId,
            EventType = "Meal Bolus",
            Insulin = 5.0,
            Carbs = 50,
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.UpdateTreatmentAsync(
                    treatmentId,
                    treatmentToUpdate,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(updatedTreatment);

        // Act
        var result = await _treatmentService.UpdateTreatmentAsync(
            treatmentId,
            treatmentToUpdate,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(treatmentId);

        // Verify MongoDB service was called
        _mockPostgreSqlService.Verify(
            x =>
                x.UpdateTreatmentAsync(
                    treatmentId,
                    treatmentToUpdate,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify WebSocket broadcasting was called
        _mockBroadcastService.Verify(
            x =>
                x.BroadcastStorageUpdateAsync(
                    "treatments",
                    It.Is<object>(o => o.ToString()!.Contains("treatments"))
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task DeleteTreatmentAsync_ShouldBroadcastStorageDeleteEvent()
    {
        // Arrange
        var treatmentId = "test-id";
        var treatmentToDelete = new Treatment
        {
            Id = treatmentId,
            EventType = "Meal Bolus",
            Insulin = 5.0,
            Carbs = 50,
        };

        _mockPostgreSqlService
            .Setup(x => x.GetTreatmentByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatmentToDelete);
        _mockPostgreSqlService
            .Setup(x => x.DeleteTreatmentAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _treatmentService.DeleteTreatmentAsync(
            treatmentId,
            CancellationToken.None
        );

        // Assert
        result.Should().BeTrue();

        // Verify MongoDB service was called
        _mockPostgreSqlService.Verify(
            x => x.GetTreatmentByIdAsync(treatmentId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockPostgreSqlService.Verify(
            x => x.DeleteTreatmentAsync(treatmentId, It.IsAny<CancellationToken>()),
            Times.Once
        ); // Verify WebSocket broadcasting was called
        _mockBroadcastService.Verify(
            x =>
                x.BroadcastStorageDeleteAsync(
                    "treatments",
                    It.Is<object>(o => o.ToString()!.Contains("treatments"))
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldHandleBroadcastException()
    {
        // Arrange
        var treatmentsToCreate = new List<Treatment>
        {
            new Treatment
            {
                EventType = "Meal Bolus",
                Insulin = 5.0,
                Carbs = 50,
            },
        };
        var createdTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "1",
                EventType = "Meal Bolus",
                Insulin = 5.0,
                Carbs = 50,
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.CreateTreatmentsAsync(treatmentsToCreate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdTreatments);

        // Setup broadcast service to throw exception
        _mockBroadcastService
            .Setup(x => x.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new Exception("Broadcast failed"));

        // Act
        var result = await _treatmentService.CreateTreatmentsAsync(
            treatmentsToCreate,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result.Should().ContainSingle();

        // Verify the treatment was still created despite broadcast failure
        _mockPostgreSqlService.Verify(
            x => x.CreateTreatmentsAsync(treatmentsToCreate, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync("treatments", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdateTreatmentAsync_WhenTreatmentNotFound_ShouldNotBroadcast()
    {
        // Arrange
        var treatmentId = "non-existent-id";
        var treatmentToUpdate = new Treatment
        {
            EventType = "Meal Bolus",
            Insulin = 5.0,
            Carbs = 50,
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.UpdateTreatmentAsync(
                    treatmentId,
                    treatmentToUpdate,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Treatment?)null);

        // Act
        var result = await _treatmentService.UpdateTreatmentAsync(
            treatmentId,
            treatmentToUpdate,
            CancellationToken.None
        );

        // Assert
        result.Should().BeNull();

        // Verify MongoDB service was called
        _mockPostgreSqlService.Verify(
            x =>
                x.UpdateTreatmentAsync(
                    treatmentId,
                    treatmentToUpdate,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify WebSocket broadcasting was NOT called
        _mockBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    public async Task DeleteTreatmentAsync_WhenTreatmentNotFound_ShouldNotBroadcast()
    {
        // Arrange
        var treatmentId = "non-existent-id";

        _mockPostgreSqlService
            .Setup(x => x.GetTreatmentByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment?)null);
        _mockPostgreSqlService
            .Setup(x => x.DeleteTreatmentAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _treatmentService.DeleteTreatmentAsync(
            treatmentId,
            CancellationToken.None
        );

        // Assert
        result.Should().BeFalse();

        // Verify WebSocket broadcasting was NOT called
        _mockBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }
}
