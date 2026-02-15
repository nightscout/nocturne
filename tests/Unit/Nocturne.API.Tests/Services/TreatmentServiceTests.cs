using System.Text.Json;
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
            .Setup(x =>
                x.GetBasalDeliveriesAsTreatmentsAsync(
                    It.IsAny<long?>(),
                    It.IsAny<long?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
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

        // Not a StateSpan temp basal
        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan?)null);

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

        // Verify PostgreSql service was called
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

        // StateSpan lookup returns null (not a temp basal)
        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan?)null);

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

        // Verify PostgreSql service was called
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

        // Not a StateSpan temp basal either
        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan?)null);

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

        // Verify PostgreSql service was called
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

        // StateSpan lookup also returns null
        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan?)null);

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

    #region StateSpan Temp Basal Tests

    [Fact]
    public async Task CreateTreatmentsAsync_TempBasal_ShouldCreateStateSpanOnly()
    {
        // Arrange
        var tempBasal = new Treatment
        {
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 30,
            Rate = 1.5,
        };

        var createdStateSpan = new StateSpan
        {
            Id = "span-id-1",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700000000000 + (30 * 60 * 1000),
            Source = "nightscout",
            Metadata = new Dictionary<string, object> { ["rate"] = 1.5 },
        };

        _mockStateSpanService
            .Setup(x =>
                x.CreateBasalDeliveryFromTreatmentAsync(
                    It.Is<Treatment>(t => t.EventType == "Temp Basal"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(createdStateSpan);

        // Act
        var result = await _treatmentService.CreateTreatmentsAsync(
            new List<Treatment> { tempBasal },
            CancellationToken.None
        );

        // Assert
        result.Should().ContainSingle();
        result.First().EventType.Should().Be("Temp Basal");

        // Verify StateSpanService was called
        _mockStateSpanService.Verify(
            x =>
                x.CreateBasalDeliveryFromTreatmentAsync(
                    It.IsAny<Treatment>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify PostgreSqlService.CreateTreatmentsAsync was NOT called (no regular treatments)
        _mockPostgreSqlService.Verify(
            x =>
                x.CreateTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task CreateTreatmentsAsync_MixedBatch_ShouldSplitCorrectly()
    {
        // Arrange
        var tempBasal = new Treatment
        {
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 30,
            Rate = 1.5,
        };
        var bolus = new Treatment { EventType = "Correction Bolus", Insulin = 2.0 };

        var createdStateSpan = new StateSpan
        {
            Id = "span-id-1",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700000000000 + (30 * 60 * 1000),
            Source = "nightscout",
            Metadata = new Dictionary<string, object> { ["rate"] = 1.5 },
        };

        _mockStateSpanService
            .Setup(x =>
                x.CreateBasalDeliveryFromTreatmentAsync(
                    It.Is<Treatment>(t => t.EventType == "Temp Basal"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(createdStateSpan);

        var createdBolus = new Treatment
        {
            Id = "bolus-1",
            EventType = "Correction Bolus",
            Insulin = 2.0,
        };
        _mockPostgreSqlService
            .Setup(x =>
                x.CreateTreatmentsAsync(
                    It.Is<IEnumerable<Treatment>>(t =>
                        t.All(tr => tr.EventType == "Correction Bolus")
                    ),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Treatment> { createdBolus });

        // Act
        var result = (
            await _treatmentService.CreateTreatmentsAsync(
                new List<Treatment> { tempBasal, bolus },
                CancellationToken.None
            )
        ).ToList();

        // Assert
        result.Should().HaveCount(2);

        // Temp basal went through StateSpanService
        _mockStateSpanService.Verify(
            x =>
                x.CreateBasalDeliveryFromTreatmentAsync(
                    It.IsAny<Treatment>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Bolus went through PostgreSqlService
        _mockPostgreSqlService.Verify(
            x =>
                x.CreateTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetTreatmentByIdAsync_WhenInTreatmentsTable_ShouldReturnDirectly()
    {
        // Arrange
        var treatmentId = "treatment-1";
        var expected = new Treatment
        {
            Id = treatmentId,
            EventType = "Meal Bolus",
            Insulin = 5.0,
        };

        _mockPostgreSqlService
            .Setup(x => x.GetTreatmentByIdAsync(treatmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        // Act
        var result = await _treatmentService.GetTreatmentByIdAsync(
            treatmentId,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(treatmentId);
        result.EventType.Should().Be("Meal Bolus");

        // Should NOT check StateSpans since treatment was found in DB
        _mockStateSpanService.Verify(
            x => x.GetStateSpanByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetTreatmentByIdAsync_WhenNotInTreatments_ShouldFallBackToStateSpan()
    {
        // Arrange
        var spanId = "span-id-1";

        _mockPostgreSqlService
            .Setup(x => x.GetTreatmentByIdAsync(spanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment?)null);

        var stateSpan = new StateSpan
        {
            Id = spanId,
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000, // +30 min
            Source = "AAPS",
            Metadata = new Dictionary<string, object> { ["rate"] = 1.5 },
        };

        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(spanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stateSpan);

        // Act
        var result = await _treatmentService.GetTreatmentByIdAsync(spanId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(1.5);
        result.Mills.Should().Be(1700000000000);
    }

    [Fact]
    public async Task GetTreatmentByIdAsync_WhenNotFoundAnywhere_ShouldReturnNull()
    {
        // Arrange
        var id = "non-existent";

        _mockPostgreSqlService
            .Setup(x => x.GetTreatmentByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment?)null);

        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan?)null);

        // Act
        var result = await _treatmentService.GetTreatmentByIdAsync(id, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task PatchTreatmentAsync_StateSpanOnly_ShouldPatchViaStateSpan()
    {
        // Arrange
        var spanId = "span-id-1";
        var patchJson = JsonSerializer.Deserialize<JsonElement>(
            """{"duration": 45, "endId": 999}"""
        );

        // Not found in treatments table
        _mockPostgreSqlService
            .Setup(x =>
                x.PatchTreatmentAsync(
                    spanId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Treatment?)null);

        var stateSpan = new StateSpan
        {
            Id = spanId,
            OriginalId = "orig-1",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000, // 30 min
            Source = "AAPS",
            Metadata = new Dictionary<string, object> { ["rate"] = 1.5, ["enteredBy"] = "AAPS" },
        };

        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(spanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stateSpan);

        _mockStateSpanService
            .Setup(x =>
                x.UpdateStateSpanAsync(spanId, It.IsAny<StateSpan>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((string _, StateSpan s, CancellationToken _) => s);

        // Act
        var result = await _treatmentService.PatchTreatmentAsync(
            spanId,
            patchJson,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result!.Duration.Should().Be(45);
        result.EndId.Should().Be(999);
        result.Rate.Should().Be(1.5); // Preserved from original

        // Verify StateSpan was updated
        _mockStateSpanService.Verify(
            x =>
                x.UpdateStateSpanAsync(
                    spanId,
                    It.IsAny<StateSpan>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task PatchTreatmentAsync_WhenNotFoundAnywhere_ShouldReturnNull()
    {
        // Arrange
        var id = "non-existent";
        var patchJson = JsonSerializer.Deserialize<JsonElement>("""{"duration": 45}""");

        _mockPostgreSqlService
            .Setup(x =>
                x.PatchTreatmentAsync(id, It.IsAny<JsonElement>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((Treatment?)null);

        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan?)null);

        // Act
        var result = await _treatmentService.PatchTreatmentAsync(
            id,
            patchJson,
            CancellationToken.None
        );

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldMergeStateSpanTempBasals()
    {
        // Arrange
        var dbTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "t1",
                EventType = "Meal Bolus",
                Mills = 1700000200000,
            },
            new Treatment
            {
                Id = "t2",
                EventType = "Correction Bolus",
                Mills = 1700000100000,
            },
        };

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
            .ReturnsAsync(dbTreatments);

        var basalTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "s1",
                EventType = "Temp Basal",
                Mills = 1700000150000,
                Rate = 1.5,
            },
        };

        _mockStateSpanService
            .Setup(x =>
                x.GetBasalDeliveriesAsTreatmentsAsync(
                    It.IsAny<long?>(),
                    It.IsAny<long?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(basalTreatments);

        // Act
        var result = (
            await _treatmentService.GetTreatmentsWithAdvancedFilterAsync(
                count: 10,
                skip: 0,
                findQuery: null,
                reverseResults: false,
                cancellationToken: CancellationToken.None
            )
        ).ToList();

        // Assert - should have all 3, merged and sorted descending by Mills
        result.Should().HaveCount(3);
        result[0].Id.Should().Be("t1"); // Mills 200000 (highest)
        result[1].Id.Should().Be("s1"); // Mills 150000 (temp basal from StateSpan)
        result[2].Id.Should().Be("t2"); // Mills 100000 (lowest)
    }

    [Fact]
    public async Task GetTreatmentsModifiedSinceAsync_ShouldIncludeStateSpanTempBasals()
    {
        // Arrange
        var dbTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "t1",
                EventType = "Meal Bolus",
                Mills = 1700000100000,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetTreatmentsModifiedSinceAsync(
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(dbTreatments);

        var basalTreatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "s1",
                EventType = "Temp Basal",
                Mills = 1700000200000,
                Rate = 1.5,
            },
        };

        _mockStateSpanService
            .Setup(x =>
                x.GetBasalDeliveriesAsTreatmentsAsync(
                    It.IsAny<long?>(),
                    It.IsAny<long?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(basalTreatments);

        // Act
        var result = (
            await _treatmentService.GetTreatmentsModifiedSinceAsync(
                lastModifiedMills: 1700000000000,
                limit: 500,
                cancellationToken: CancellationToken.None
            )
        ).ToList();

        // Assert - merged and sorted ascending by Mills
        result.Should().HaveCount(2);
        result[0].Id.Should().Be("t1"); // Mills 100000 (lowest first - ascending)
        result[1].Id.Should().Be("s1"); // Mills 200000 (temp basal from StateSpan)
    }

    [Fact]
    public async Task DeleteTreatmentAsync_StateSpanTempBasal_ShouldDeleteFromStateSpan()
    {
        // Arrange
        var spanId = "span-id-1";

        var stateSpan = new StateSpan
        {
            Id = spanId,
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "AAPS",
            Metadata = new Dictionary<string, object> { ["rate"] = 1.5 },
        };

        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(spanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stateSpan);

        _mockStateSpanService
            .Setup(x => x.DeleteStateSpanAsync(spanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _treatmentService.DeleteTreatmentAsync(spanId, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        // Verify StateSpan was deleted
        _mockStateSpanService.Verify(
            x => x.DeleteStateSpanAsync(spanId, It.IsAny<CancellationToken>()),
            Times.Once
        );

        // Verify PostgreSqlService.DeleteTreatmentAsync was NOT called
        _mockPostgreSqlService.Verify(
            x => x.DeleteTreatmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );

        // Verify broadcast was sent
        _mockBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("treatments", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdateTreatmentAsync_StateSpanTempBasal_ShouldUpdateStateSpan()
    {
        // Arrange
        var spanId = "span-id-1";
        var treatmentUpdate = new Treatment
        {
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 45,
            Rate = 2.0,
        };

        var existingStateSpan = new StateSpan
        {
            Id = spanId,
            OriginalId = "orig-1",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "AAPS",
            Metadata = new Dictionary<string, object> { ["rate"] = 1.5 },
        };

        _mockStateSpanService
            .Setup(x => x.GetStateSpanByIdAsync(spanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingStateSpan);

        var updatedStateSpan = new StateSpan
        {
            Id = spanId,
            OriginalId = "orig-1",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700000000000 + (45 * 60 * 1000),
            Source = "AAPS",
            Metadata = new Dictionary<string, object> { ["rate"] = 2.0 },
        };

        _mockStateSpanService
            .Setup(x =>
                x.UpdateStateSpanAsync(spanId, It.IsAny<StateSpan>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(updatedStateSpan);

        // Act
        var result = await _treatmentService.UpdateTreatmentAsync(
            spanId,
            treatmentUpdate,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result!.EventType.Should().Be("Temp Basal");

        // Verify StateSpan was updated (not PostgreSql)
        _mockStateSpanService.Verify(
            x =>
                x.UpdateStateSpanAsync(
                    spanId,
                    It.IsAny<StateSpan>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        _mockPostgreSqlService.Verify(
            x =>
                x.UpdateTreatmentAsync(
                    It.IsAny<string>(),
                    It.IsAny<Treatment>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    #endregion
}
