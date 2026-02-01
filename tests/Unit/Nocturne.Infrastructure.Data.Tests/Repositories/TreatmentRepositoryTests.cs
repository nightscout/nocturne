using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Comprehensive tests for TreatmentRepository focusing on PostgreSQL functionality
/// with MongoDB-style query support, CRUD operations, edge cases, and performance scenarios
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "Treatment")]
public class TreatmentRepositoryTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Mock<IDeduplicationService> _mockDeduplicationService;
    private readonly Mock<ILogger<TreatmentRepository>> _mockLogger;

    public TreatmentRepositoryTests()
    {
        _mockDeduplicationService = new Mock<IDeduplicationService>();
        _mockLogger = new Mock<ILogger<TreatmentRepository>>();
        // Create in-memory SQLite database for testing
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        // Create the database schema
        using var context = new NocturneDbContext(_contextOptions);
        context.Database.EnsureCreated();

        // Setup mocks for repository dependencies
        _mockDeduplicationService = new Mock<IDeduplicationService>();
        _mockDeduplicationService
            .Setup(d =>
                d.GetOrCreateCanonicalIdAsync(
                    It.IsAny<RecordType>(),
                    It.IsAny<long>(),
                    It.IsAny<MatchCriteria>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Guid.NewGuid());
        _mockDeduplicationService
            .Setup(d =>
                d.LinkRecordAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<RecordType>(),
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);

        _mockLogger = new Mock<ILogger<TreatmentRepository>>();
    }

    private TreatmentRepository CreateRepository(
        NocturneDbContext context,
        IQueryParser queryParser
    )
    {
        return new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );
    }

    #region CRUD Operations Tests

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldCreateSingleTreatment_WhenValidDataProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 2.5, eventType: "Correction Bolus");
        var treatments = new[] { treatment };

        // Act
        var result = await repository.CreateTreatmentsAsync(treatments);

        // Assert
        result.Should().HaveCount(1);
        var createdTreatment = result.First();
        createdTreatment.Insulin.Should().Be(2.5);
        createdTreatment.EventType.Should().Be("Correction Bolus");
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldCreateMultipleTreatments_WhenMultipleTreatmentsProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var treatments = new[]
        {
            CreateTestTreatment(
                insulin: 3.0,
                eventType: "Meal Bolus",
                mills: baseTime.ToUnixTimeMilliseconds()
            ),
            CreateTestTreatment(
                insulin: 1.5,
                eventType: "Correction Bolus",
                mills: baseTime.AddMinutes(-30).ToUnixTimeMilliseconds()
            ),
            CreateTestTreatment(
                carbs: 45.0,
                eventType: "Carb Correction",
                mills: baseTime.AddMinutes(-60).ToUnixTimeMilliseconds()
            ),
        };

        // Act
        var result = await repository.CreateTreatmentsAsync(treatments);

        // Assert
        result.Should().HaveCount(3);
        result
            .Select(t => t.EventType)
            .Should()
            .BeEquivalentTo(new[] { "Meal Bolus", "Correction Bolus", "Carb Correction" });
        result.Should().BeInDescendingOrder(t => t.Mills);
    }

    [Fact]
    public async Task GetTreatmentByIdAsync_ShouldReturnTreatment_WhenValidOriginalIdProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 4.0, eventType: "Meal Bolus");
        await repository.CreateTreatmentsAsync(new[] { treatment });

        // Act
        var result = await repository.GetTreatmentByIdAsync(treatment.Id!);

        // Assert
        result.Should().NotBeNull();
        result!.Insulin.Should().Be(4.0);
        result.EventType.Should().Be("Meal Bolus");
        result.Id.Should().Be(treatment.Id);
    }

    [Fact]
    public async Task GetTreatmentByIdAsync_ShouldReturnTreatment_WhenValidGuidIdProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 2.0, eventType: "Correction Bolus");
        await repository.CreateTreatmentsAsync(new[] { treatment });

        // Get the actual entity from the database - when treatment.Id is a GUID string,
        // it becomes the entity's Id but OriginalId is null (only set for MongoDB ObjectIds)
        var entity = await context.Treatments.FirstAsync(t =>
            t.Insulin == 2.0 && t.EventType == "Correction Bolus"
        );
        var guidId = entity.Id.ToString();

        // Act
        var result = await repository.GetTreatmentByIdAsync(guidId);

        // Assert
        result.Should().NotBeNull();
        result!.Insulin.Should().Be(2.0);
        result.EventType.Should().Be("Correction Bolus");
    }

    [Fact]
    public async Task GetTreatmentByIdAsync_ShouldReturnNull_WhenTreatmentNotFound()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var result = await repository.GetTreatmentByIdAsync(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateTreatmentAsync_ShouldUpdateTreatment_WhenValidIdAndDataProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 2.0, eventType: "Correction Bolus");
        await repository.CreateTreatmentsAsync(new[] { treatment });

        var updatedTreatment = new Treatment
        {
            Id = treatment.Id,
            Mills = treatment.Mills,
            Created_at = treatment.Created_at,
            Insulin = 3.5,
            EventType = "Meal Bolus",
            Notes = "Updated treatment notes",
            EnteredBy = treatment.EnteredBy,
        };

        // Act
        var result = await repository.UpdateTreatmentAsync(treatment.Id!, updatedTreatment);

        // Assert
        result.Should().NotBeNull();
        result!.Insulin.Should().Be(3.5);
        result.EventType.Should().Be("Meal Bolus");
        result.Notes.Should().Be("Updated treatment notes");
        result.Id.Should().Be(treatment.Id);
    }

    [Fact]
    public async Task UpdateTreatmentAsync_ShouldReturnNull_WhenTreatmentNotFound()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var nonExistentId = Guid.NewGuid().ToString();
        var treatment = CreateTestTreatment();

        // Act
        var result = await repository.UpdateTreatmentAsync(nonExistentId, treatment);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteTreatmentAsync_ShouldDeleteTreatment_WhenValidIdProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 1.5);
        await repository.CreateTreatmentsAsync(new[] { treatment });

        // Act
        var result = await repository.DeleteTreatmentAsync(treatment.Id!);

        // Assert
        result.Should().BeTrue();

        // Verify the treatment is actually deleted
        var deletedTreatment = await repository.GetTreatmentByIdAsync(treatment.Id!);
        deletedTreatment.Should().BeNull();
    }

    [Fact]
    public async Task DeleteTreatmentAsync_ShouldReturnFalse_WhenTreatmentNotFound()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var result = await repository.DeleteTreatmentAsync(nonExistentId);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Query and Filtering Tests

    [Fact]
    public async Task GetTreatmentsAsync_ShouldReturnTreatmentsInDescendingOrder_ByDefault()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var treatments = new[]
        {
            CreateTestTreatment(
                insulin: 1.0,
                mills: baseTime.AddMinutes(-60).ToUnixTimeMilliseconds()
            ),
            CreateTestTreatment(
                insulin: 2.0,
                mills: baseTime.AddMinutes(-30).ToUnixTimeMilliseconds()
            ),
            CreateTestTreatment(insulin: 3.0, mills: baseTime.ToUnixTimeMilliseconds()),
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var result = await repository.GetTreatmentsAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(t => t.Mills);
        result.First().Insulin.Should().Be(3.0); // Most recent
        result.Last().Insulin.Should().Be(1.0); // Oldest
    }

    [Fact]
    public async Task GetTreatmentsAsync_ShouldFilterByEventType_WhenEventTypeSpecified()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 3.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 1.5, eventType: "Correction Bolus"),
            CreateTestTreatment(insulin: 2.0, eventType: "Meal Bolus"),
            CreateTestTreatment(carbs: 30.0, eventType: "Carb Correction"),
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var mealBoluses = await repository.GetTreatmentsAsync(eventType: "Meal Bolus");
        var correctionBoluses = await repository.GetTreatmentsAsync(eventType: "Correction Bolus");

        // Assert
        mealBoluses.Should().HaveCount(2);
        mealBoluses.All(t => t.EventType == "Meal Bolus").Should().BeTrue();

        correctionBoluses.Should().HaveCount(1);
        correctionBoluses.All(t => t.EventType == "Correction Bolus").Should().BeTrue();
    }

    [Fact]
    public async Task GetTreatmentsAsync_ShouldRespectPagination_WhenCountAndSkipProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var treatments = Enumerable
            .Range(1, 10)
            .Select(i =>
                CreateTestTreatment(
                    insulin: i * 0.5,
                    mills: baseTime.AddMinutes(-i).ToUnixTimeMilliseconds()
                )
            )
            .ToArray();

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var firstPage = await repository.GetTreatmentsAsync(count: 3, skip: 0);
        var secondPage = await repository.GetTreatmentsAsync(count: 3, skip: 3);

        // Assert
        firstPage.Should().HaveCount(3);
        secondPage.Should().HaveCount(3);

        // Verify no overlap
        var firstPageIds = firstPage.Select(t => t.Id).ToHashSet();
        var secondPageIds = secondPage.Select(t => t.Id).ToHashSet();
        firstPageIds.Should().NotIntersectWith(secondPageIds);
    }

    #endregion

    #region Advanced Filtering and MongoDB-Style Query Tests

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterByDateString_WhenProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var filterTime = baseTime.AddHours(-1);

        var treatments = new[]
        {
            CreateTestTreatment(
                insulin: 1.0,
                mills: baseTime.AddHours(-2).ToUnixTimeMilliseconds()
            ), // Before filter
            CreateTestTreatment(
                insulin: 2.0,
                mills: baseTime.AddMinutes(-30).ToUnixTimeMilliseconds()
            ), // After filter
            CreateTestTreatment(insulin: 3.0, mills: baseTime.ToUnixTimeMilliseconds()), // After filter
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            dateString: filterTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        );

        // Assert
        result.Should().HaveCount(2);
        result.All(t => t.Mills >= filterTime.ToUnixTimeMilliseconds()).Should().BeTrue();
        result.Select(t => t.Insulin).Should().BeEquivalentTo(new[] { 3.0, 2.0 }); // Descending order
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldReverseOrder_WhenReverseResultsTrue()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var treatments = new[]
        {
            CreateTestTreatment(
                insulin: 1.0,
                mills: baseTime.AddMinutes(-20).ToUnixTimeMilliseconds()
            ),
            CreateTestTreatment(
                insulin: 2.0,
                mills: baseTime.AddMinutes(-10).ToUnixTimeMilliseconds()
            ),
            CreateTestTreatment(insulin: 3.0, mills: baseTime.ToUnixTimeMilliseconds()),
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var normalOrder = await repository.GetTreatmentsWithAdvancedFilterAsync(
            reverseResults: false
        );
        var reversedOrder = await repository.GetTreatmentsWithAdvancedFilterAsync(
            reverseResults: true
        );

        // Assert
        normalOrder.Should().BeInDescendingOrder(t => t.Mills);
        reversedOrder.Should().BeInAscendingOrder(t => t.Mills);

        normalOrder.First().Insulin.Should().Be(3.0);
        reversedOrder.First().Insulin.Should().Be(1.0);
    }

    #region MongoDB Query Filtering Tests

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterByEventType_WhenEventTypeQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 2.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 1.5, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 1.0, eventType: "Correction Bolus"),
            CreateTestTreatment(carbs: 30.0, eventType: "Carb Correction"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"eventType\":\"Meal Bolus\"}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.EventType == "Meal Bolus");
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterByInsulinRange_WhenGteAndLteProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 0.5, eventType: "Correction Bolus"),
            CreateTestTreatment(insulin: 1.5, eventType: "Correction Bolus"),
            CreateTestTreatment(insulin: 2.5, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 4.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 6.0, eventType: "Meal Bolus"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - Filter treatments with insulin between 1.0 and 3.0 inclusive
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"insulin\":{\"$gte\":1.0,\"$lte\":3.0}}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.Insulin >= 1.0 && t.Insulin <= 3.0);
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterByEventTypeAndInsulin_WhenCombinedQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 1.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 3.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 5.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 3.0, eventType: "Correction Bolus"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - Filter Meal Bolus with insulin >= 2.0
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"eventType\":\"Meal Bolus\",\"insulin\":{\"$gte\":2.0}}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.EventType == "Meal Bolus" && t.Insulin >= 2.0);
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterWithOrOperator_WhenOrQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 2.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 1.0, eventType: "Correction Bolus"),
            CreateTestTreatment(carbs: 30.0, eventType: "Carb Correction"),
            CreateTestTreatment(eventType: "Site Change"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - $or: eventType=Meal Bolus OR eventType=Correction Bolus
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"$or\":[{\"eventType\":\"Meal Bolus\"},{\"eventType\":\"Correction Bolus\"}]}"
        );

        // Assert
        result.Should().HaveCount(2);
        result
            .Should()
            .OnlyContain(t => t.EventType == "Meal Bolus" || t.EventType == "Correction Bolus");
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterWithAndOperator_WhenAndQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 1.0, carbs: 20.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 2.0, carbs: 40.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 3.0, carbs: 10.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 2.0, eventType: "Correction Bolus"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - $and: insulin >= 2.0 AND carbs >= 30.0
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"$and\":[{\"insulin\":{\"$gte\":2.0}},{\"carbs\":{\"$gte\":30.0}}]}"
        );

        // Assert
        result.Should().HaveCount(1);
        result.First().Insulin.Should().Be(2.0);
        result.First().Carbs.Should().Be(40.0);
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterWithUrlEncodedQuery_WhenUrlFormatProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 1.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 3.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 2.0, eventType: "Correction Bolus"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - URL-encoded format: find[eventType]=Meal Bolus&find[insulin][$gte]=2.0
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "find[eventType]=Meal Bolus&find[insulin][$gte]=2.0"
        );

        // Assert
        result.Should().HaveCount(1);
        result.First().Insulin.Should().Be(3.0);
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldReturnEmpty_WhenNoMatchingTreatments()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 2.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 1.0, eventType: "Correction Bolus"),
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - Query for non-existent event type
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"eventType\":\"Nonexistent Type\"}"
        );

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldFilterByCarbsGreaterThan_WhenGtProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(carbs: 10.0, eventType: "Carb Correction"),
            CreateTestTreatment(carbs: 30.0, eventType: "Meal Bolus"),
            CreateTestTreatment(carbs: 50.0, eventType: "Meal Bolus"),
            CreateTestTreatment(eventType: "Site Change"), // No carbs
        };
        await repository.CreateTreatmentsAsync(treatments);

        // Act - Filter for carbs > 20
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            findQuery: "{\"carbs\":{\"$gt\":20.0}}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.Carbs > 20.0);
    }

    #endregion


    [Fact]
    public async Task CountTreatmentsAsync_ShouldReturnCorrectCount_WhenTreatmentsExist()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 1.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 2.0, eventType: "Correction Bolus"),
            CreateTestTreatment(carbs: 30.0, eventType: "Carb Correction"),
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var totalCount = await repository.CountTreatmentsAsync();

        // Assert
        totalCount.Should().Be(3);
    }

    [Fact]
    public async Task CountTreatmentsAsync_ShouldReturnZero_WhenNoTreatmentsExist()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        // Act
        var count = await repository.CountTreatmentsAsync();

        // Assert
        count.Should().Be(0);
    }

    #endregion

    #region Bulk Operations Tests

    [Fact]
    public async Task DeleteTreatmentsAsync_ShouldDeleteAllTreatments_WhenNoEventTypeSpecified()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 1.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 2.0, eventType: "Correction Bolus"),
            CreateTestTreatment(carbs: 30.0, eventType: "Carb Correction"),
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var deletedCount = await repository.DeleteTreatmentsAsync();

        // Assert
        deletedCount.Should().Be(3);

        var remainingCount = await repository.CountTreatmentsAsync();
        remainingCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_ShouldDeleteOnlySpecifiedEventType_WhenEventTypeProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = new[]
        {
            CreateTestTreatment(insulin: 1.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 2.0, eventType: "Meal Bolus"),
            CreateTestTreatment(insulin: 1.5, eventType: "Correction Bolus"),
        };

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var deletedCount = await repository.DeleteTreatmentsAsync(eventType: "Meal Bolus");

        // Assert
        deletedCount.Should().Be(2);

        var remainingCount = await repository.CountTreatmentsAsync();
        remainingCount.Should().Be(1);

        var remainingTreatments = await repository.GetTreatmentsAsync();
        remainingTreatments.First().EventType.Should().Be("Correction Bolus");
    }

    #endregion

    #region Edge Cases and Error Handling Tests

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldHandleEmptyCollection_Gracefully()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        // Act
        var result = await repository.CreateTreatmentsAsync(Array.Empty<Treatment>());

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldHandleTreatmentsWithNullValues_Appropriately()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 2.0);
        treatment.EventType = null;
        treatment.Notes = null;
        treatment.EnteredBy = null;
        treatment.Carbs = null;

        // Act
        var result = await repository.CreateTreatmentsAsync(new[] { treatment });

        // Assert
        result.Should().HaveCount(1);
        var createdTreatment = result.First();
        createdTreatment.EventType.Should().BeNull();
        createdTreatment.Notes.Should().BeNull();
        createdTreatment.EnteredBy.Should().BeNull();
        createdTreatment.Carbs.Should().BeNull();
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldHandleTreatmentsWithComplexData_Appropriately()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment();
        treatment.Insulin = 4.75;
        treatment.Carbs = 62.5;
        treatment.Protein = 15.3;
        treatment.Fat = 8.7;
        treatment.Duration = 120.5;
        treatment.Percent = 75.0;
        treatment.BolusCalc = new Dictionary<string, object>
        {
            ["carbs"] = 60,
            ["cob"] = 15.5,
            ["iob"] = 1.2,
        };

        // Act
        var result = await repository.CreateTreatmentsAsync(new[] { treatment });

        // Assert
        result.Should().HaveCount(1);
        var createdTreatment = result.First();
        createdTreatment.Insulin.Should().Be(4.75);
        createdTreatment.Carbs.Should().Be(62.5);
        createdTreatment.Protein.Should().Be(15.3);
        createdTreatment.Fat.Should().Be(8.7);
        createdTreatment.Duration.Should().Be(120.5);
        createdTreatment.Percent.Should().Be(75.0);
        createdTreatment.BolusCalc.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTreatmentsWithAdvancedFilterAsync_ShouldHandleInvalidDateString_Gracefully()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment = CreateTestTreatment(insulin: 2.0);
        await repository.CreateTreatmentsAsync(new[] { treatment });

        // Act - Should not throw with invalid date
        var result = await repository.GetTreatmentsWithAdvancedFilterAsync(
            dateString: "invalid-date-string"
        );

        // Assert
        result.Should().HaveCount(1); // Should return all treatments when date parsing fails
    }

    [Theory]
    [InlineData(0, 10)] // Normal pagination
    [InlineData(5, 0)] // Zero count
    [InlineData(0, 1000)] // Large count
    [InlineData(100, 10)] // Skip beyond available data
    public async Task GetTreatmentsAsync_ShouldHandleVariousPaginationValues_Appropriately(
        int skip,
        int count
    )
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatments = Enumerable
            .Range(1, 20)
            .Select(i => CreateTestTreatment(insulin: i * 0.5))
            .ToArray();

        await repository.CreateTreatmentsAsync(treatments);

        // Act
        var result = await repository.GetTreatmentsAsync(count: count, skip: skip);

        // Assert
        if (count == 0)
        {
            result.Should().BeEmpty();
        }
        else if (skip >= 20)
        {
            result.Should().BeEmpty();
        }
        else
        {
            result.Should().HaveCount(Math.Min(count, Math.Max(0, 20 - skip)));
        }
    }

    #endregion

    #region Performance and Large Dataset Tests

    [Fact]
    [Trait("Category", "Performance")]
    public async Task GetTreatmentsAsync_ShouldPerformWell_WithLargeDataset()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var eventTypes = new[] { "Meal Bolus", "Correction Bolus", "Carb Correction", "BG Check" };
        var largeTreatmentSet = Enumerable
            .Range(1, 1000)
            .Select(i =>
                CreateTestTreatment(
                    insulin: 0.5 + (i % 10) * 0.5, // Vary insulin values
                    eventType: eventTypes[i % eventTypes.Length], // Mix of event types
                    mills: baseTime.AddMinutes(-i).ToUnixTimeMilliseconds()
                )
            )
            .ToArray();

        await repository.CreateTreatmentsAsync(largeTreatmentSet);

        // Act & Assert - Should complete in reasonable time
        var start = DateTimeOffset.UtcNow;

        var pagedResults = await repository.GetTreatmentsAsync(count: 50, skip: 0);
        var filteredResults = await repository.GetTreatmentsAsync(
            eventType: "Meal Bolus",
            count: 100,
            skip: 0
        );
        var countResult = await repository.CountTreatmentsAsync();

        var elapsed = DateTimeOffset.UtcNow - start;

        // Assert results
        pagedResults.Should().HaveCount(50);
        filteredResults.Should().HaveCount(100);
        filteredResults.All(t => t.EventType == "Meal Bolus").Should().BeTrue();
        countResult.Should().Be(1000);

        // Performance assertion - should complete within reasonable time
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CreateTreatmentsAsync_ShouldHandleBatchInsert_Efficiently()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var batchSize = 100;
        var treatments = Enumerable
            .Range(1, batchSize)
            .Select(i => CreateTestTreatment(insulin: 0.5 + i * 0.1))
            .ToArray();

        // Act
        var start = DateTimeOffset.UtcNow;
        var result = await repository.CreateTreatmentsAsync(treatments);
        var elapsed = DateTimeOffset.UtcNow - start;

        // Assert
        result.Should().HaveCount(batchSize);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2)); // Should be reasonably fast
    }

    #endregion

    #region Treatment-Specific Tests

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldHandleDifferentTreatmentTypes_Correctly()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var treatment1 = CreateTestTreatment();
        treatment1.EventType = "Meal Bolus";
        treatment1.Insulin = 4.5;
        treatment1.Carbs = 60.0;
        treatment1.Notes = "Lunch bolus";

        var treatment2 = CreateTestTreatment();
        treatment2.EventType = "Correction Bolus";
        treatment2.Insulin = 1.5;
        treatment2.Glucose = 180.0;
        treatment2.GlucoseType = "Finger";

        var treatment3 = CreateTestTreatment();
        treatment3.EventType = "BG Check";
        treatment3.Insulin = null;
        treatment3.Glucose = 95.0;
        treatment3.GlucoseType = "Finger";

        var treatment4 = CreateTestTreatment();
        treatment4.EventType = "Temp Basal";
        treatment4.Duration = 60.0;
        treatment4.Percent = 120.0;
        treatment4.Absolute = 1.2;

        var treatments = new[] { treatment1, treatment2, treatment3, treatment4 };

        // Act
        var result = await repository.CreateTreatmentsAsync(treatments);

        // Assert
        result.Should().HaveCount(4);

        var mealBolus = result.First(t => t.EventType == "Meal Bolus");
        mealBolus.Insulin.Should().Be(4.5);
        mealBolus.Carbs.Should().Be(60.0);

        var correctionBolus = result.First(t => t.EventType == "Correction Bolus");
        correctionBolus.Insulin.Should().Be(1.5);
        correctionBolus.Glucose.Should().Be(180.0);

        var bgCheck = result.First(t => t.EventType == "BG Check");
        bgCheck.Insulin.Should().BeNull();
        bgCheck.Glucose.Should().Be(95.0);

        var tempBasal = result.First(t => t.EventType == "Temp Basal");
        tempBasal.Duration.Should().Be(60.0);
        tempBasal.Percent.Should().Be(120.0);
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldCalculateMills_FromCreatedAtWhenMissing()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new TreatmentRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var testTime = DateTimeOffset.UtcNow.AddHours(-1);
        var treatment = CreateTestTreatment();
        treatment.Mills = 0; // No mills provided
        treatment.Created_at = testTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // Act
        var result = await repository.CreateTreatmentsAsync(new[] { treatment });

        // Assert
        result.Should().HaveCount(1);
        var createdTreatment = result.First();
        createdTreatment.Mills.Should().Be(testTime.ToUnixTimeMilliseconds());
    }

    #endregion

    #region Test Helper Methods

    private static Treatment CreateTestTreatment(
        double? insulin = 2.0,
        string eventType = "Correction Bolus",
        long? mills = null,
        string? id = null,
        double? carbs = null
    )
    {
        var timestamp = mills ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Treatment
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Mills = timestamp,
            Created_at = DateTimeOffset
                .FromUnixTimeMilliseconds(timestamp)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            EventType = eventType,
            Insulin = insulin,
            Carbs = carbs,
            Notes = "Test treatment",
            EnteredBy = "test-user",
        };
    }

    #endregion

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
