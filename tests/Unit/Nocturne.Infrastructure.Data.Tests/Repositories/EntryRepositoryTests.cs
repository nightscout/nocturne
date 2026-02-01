using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Comprehensive tests for EntryRepository focusing on PostgreSQL functionality
/// with MongoDB-style query support, CRUD operations, edge cases, and performance scenarios
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "Entry")]
public class EntryRepositoryTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Mock<IDeduplicationService> _mockDeduplicationService;
    private readonly Mock<ILogger<EntryRepository>> _mockLogger;

    public EntryRepositoryTests()
    {
        _mockDeduplicationService = new Mock<IDeduplicationService>();
        _mockLogger = new Mock<ILogger<EntryRepository>>();
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

        _mockLogger = new Mock<ILogger<EntryRepository>>();
    }

    private EntryRepository CreateRepository(NocturneDbContext context, IQueryParser queryParser)
    {
        return new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );
    }

    #region CRUD Operations Tests

    [Fact]
    public async Task CreateEntriesAsync_ShouldCreateSingleEntry_WhenValidDataProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 120.5, type: "sgv");
        var entries = new[] { entry };

        // Act
        var result = await repository.CreateEntriesAsync(entries);

        // Assert
        result.Should().HaveCount(1);
        var createdEntry = result.First();
        createdEntry.Mgdl.Should().Be(120.5);
        createdEntry.Type.Should().Be("sgv");
        createdEntry.Direction.Should().Be("Flat");
    }

    [Fact]
    public async Task CreateEntriesAsync_ShouldCreateMultipleEntries_WhenMultipleEntriesProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(
                sgv: 120.5,
                type: "sgv",
                mills: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            ),
            CreateTestEntry(
                sgv: 115.0,
                type: "mbg",
                mills: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()
            ),
            CreateTestEntry(
                sgv: 110.0,
                type: "cal",
                mills: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()
            ),
        };

        // Act
        var result = await repository.CreateEntriesAsync(entries);

        // Assert
        result.Should().HaveCount(3);
        result.Select(e => e.Type).Should().BeEquivalentTo(new[] { "sgv", "mbg", "cal" });
        result.Should().BeInDescendingOrder(e => e.Mills);
    }

    [Fact]
    public async Task GetEntryByIdAsync_ShouldReturnEntry_WhenValidOriginalIdProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 125.0);
        await repository.CreateEntriesAsync(new[] { entry });

        // Act
        var result = await repository.GetEntryByIdAsync(entry.Id!);

        // Assert
        result.Should().NotBeNull();
        result!.Mgdl.Should().Be(125.0);
        result.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task GetEntryByIdAsync_ShouldReturnEntry_WhenValidGuidIdProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 130.0);
        await repository.CreateEntriesAsync(new[] { entry });

        // Get the actual entity from the database - when entry.Id is a GUID string,
        // it becomes the entity's Id but OriginalId is null (only set for MongoDB ObjectIds)
        var entity = await context.Entries.FirstAsync(e => e.Mgdl == 130.0);
        var guidId = entity.Id.ToString();

        // Act
        var result = await repository.GetEntryByIdAsync(guidId);

        // Assert
        result.Should().NotBeNull();
        result!.Mgdl.Should().Be(130.0);
    }

    [Fact]
    public async Task GetEntryByIdAsync_ShouldReturnNull_WhenEntryNotFound()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var result = await repository.GetEntryByIdAsync(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateEntryAsync_ShouldUpdateEntry_WhenValidIdAndDataProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 120.0);
        await repository.CreateEntriesAsync(new[] { entry });

        var updatedEntry = new Entry
        {
            Id = entry.Id,
            Mills = entry.Mills,
            DateString = entry.DateString,
            Mgdl = 135.0,
            Sgv = 135.0,
            Direction = "SingleUp",
            Type = entry.Type,
            Device = entry.Device,
            Delta = entry.Delta,
            Rssi = entry.Rssi,
            Noise = entry.Noise,
            CreatedAt = entry.CreatedAt,
        };

        // Act
        var result = await repository.UpdateEntryAsync(entry.Id!, updatedEntry);

        // Assert
        result.Should().NotBeNull();
        result!.Mgdl.Should().Be(135.0);
        result.Direction.Should().Be("SingleUp");
        result.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task UpdateEntryAsync_ShouldReturnNull_WhenEntryNotFound()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var nonExistentId = Guid.NewGuid().ToString();
        var entry = CreateTestEntry();

        // Act
        var result = await repository.UpdateEntryAsync(nonExistentId, entry);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteEntryAsync_ShouldDeleteEntry_WhenValidIdProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 140.0);
        await repository.CreateEntriesAsync(new[] { entry });

        // Act
        var result = await repository.DeleteEntryAsync(entry.Id!);

        // Assert
        result.Should().BeTrue();

        // Verify the entry is actually deleted
        var deletedEntry = await repository.GetEntryByIdAsync(entry.Id!);
        deletedEntry.Should().BeNull();
    }

    [Fact]
    public async Task DeleteEntryAsync_ShouldReturnFalse_WhenEntryNotFound()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var result = await repository.DeleteEntryAsync(nonExistentId);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Query and Filtering Tests

    [Fact]
    public async Task GetEntriesAsync_ShouldReturnEntriesInDescendingOrder_ByDefault()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            CreateTestEntry(sgv: 120.0, mills: baseTime.AddMinutes(-10).ToUnixTimeMilliseconds()),
            CreateTestEntry(sgv: 125.0, mills: baseTime.AddMinutes(-5).ToUnixTimeMilliseconds()),
            CreateTestEntry(sgv: 130.0, mills: baseTime.ToUnixTimeMilliseconds()),
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var result = await repository.GetEntriesAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Should().BeInDescendingOrder(e => e.Mills);
        result.First().Mgdl.Should().Be(130.0); // Most recent
        result.Last().Mgdl.Should().Be(120.0); // Oldest
    }

    [Fact]
    public async Task GetEntriesAsync_ShouldFilterByType_WhenTypeSpecified()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 120.0, type: "sgv"),
            CreateTestEntry(sgv: 115.0, type: "mbg"),
            CreateTestEntry(sgv: 110.0, type: "sgv"),
            CreateTestEntry(sgv: 105.0, type: "cal"),
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var sgvEntries = await repository.GetEntriesAsync(type: "sgv");
        var mbgEntries = await repository.GetEntriesAsync(type: "mbg");

        // Assert
        sgvEntries.Should().HaveCount(2);
        sgvEntries.All(e => e.Type == "sgv").Should().BeTrue();

        mbgEntries.Should().HaveCount(1);
        mbgEntries.All(e => e.Type == "mbg").Should().BeTrue();
    }

    [Fact]
    public async Task GetEntriesAsync_ShouldRespectPagination_WhenCountAndSkipProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var entries = Enumerable
            .Range(1, 10)
            .Select(i =>
                CreateTestEntry(
                    sgv: 100.0 + i,
                    mills: baseTime.AddMinutes(-i).ToUnixTimeMilliseconds()
                )
            )
            .ToArray();

        await repository.CreateEntriesAsync(entries);

        // Act
        var firstPage = await repository.GetEntriesAsync(count: 3, skip: 0);
        var secondPage = await repository.GetEntriesAsync(count: 3, skip: 3);

        // Assert
        firstPage.Should().HaveCount(3);
        secondPage.Should().HaveCount(3);

        // Verify no overlap
        var firstPageIds = firstPage.Select(e => e.Id).ToHashSet();
        var secondPageIds = secondPage.Select(e => e.Id).ToHashSet();
        firstPageIds.Should().NotIntersectWith(secondPageIds);
    }

    [Fact]
    public async Task GetCurrentEntryAsync_ShouldReturnMostRecentEntry()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            CreateTestEntry(sgv: 120.0, mills: baseTime.AddMinutes(-10).ToUnixTimeMilliseconds()),
            CreateTestEntry(sgv: 125.0, mills: baseTime.AddMinutes(-5).ToUnixTimeMilliseconds()),
            CreateTestEntry(sgv: 130.0, mills: baseTime.ToUnixTimeMilliseconds()), // Most recent
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var result = await repository.GetCurrentEntryAsync();

        // Assert
        result.Should().NotBeNull();
        result!.Mgdl.Should().Be(130.0);
        result.Mills.Should().Be(baseTime.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task GetCurrentEntryAsync_ShouldReturnNull_WhenNoEntriesExist()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        // Act
        var result = await repository.GetCurrentEntryAsync();

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Advanced Filtering and MongoDB-Style Query Tests

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterByDateString_WhenProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var filterTime = baseTime.AddHours(-1);

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, mills: baseTime.AddHours(-2).ToUnixTimeMilliseconds()), // Before filter
            CreateTestEntry(sgv: 110.0, mills: baseTime.AddMinutes(-30).ToUnixTimeMilliseconds()), // After filter
            CreateTestEntry(sgv: 120.0, mills: baseTime.ToUnixTimeMilliseconds()), // After filter
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            dateString: filterTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        );

        // Assert
        result.Should().HaveCount(2);
        result.All(e => e.Mills >= filterTime.ToUnixTimeMilliseconds()).Should().BeTrue();
        result.Select(e => e.Mgdl).Should().BeEquivalentTo(new[] { 120.0, 110.0 }); // Descending order
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldReverseOrder_WhenReverseResultsTrue()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, mills: baseTime.AddMinutes(-10).ToUnixTimeMilliseconds()),
            CreateTestEntry(sgv: 110.0, mills: baseTime.AddMinutes(-5).ToUnixTimeMilliseconds()),
            CreateTestEntry(sgv: 120.0, mills: baseTime.ToUnixTimeMilliseconds()),
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var normalOrder = await repository.GetEntriesWithAdvancedFilterAsync(reverseResults: false);
        var reversedOrder = await repository.GetEntriesWithAdvancedFilterAsync(
            reverseResults: true
        );

        // Assert
        normalOrder.Should().BeInDescendingOrder(e => e.Mills);
        reversedOrder.Should().BeInAscendingOrder(e => e.Mills);

        normalOrder.First().Mgdl.Should().Be(120.0);
        reversedOrder.First().Mgdl.Should().Be(100.0);
    }

    #region MongoDB Query Filtering Tests

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterByType_WhenTypeQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 110.0, type: "sgv"),
            CreateTestEntry(sgv: 120.0, type: "mbg"),
            CreateTestEntry(sgv: 130.0, type: "cal"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"type\":\"sgv\"}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Type == "sgv");
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterBySgvRange_WhenGteAndLteProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 80.0, type: "sgv"),
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 150.0, type: "sgv"),
            CreateTestEntry(sgv: 200.0, type: "sgv"),
            CreateTestEntry(sgv: 250.0, type: "sgv"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - Filter entries between 100 and 200 inclusive
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"sgv\":{\"$gte\":100,\"$lte\":200}}"
        );

        // Assert
        result.Should().HaveCount(3);
        result.Should().OnlyContain(e => e.Sgv >= 100 && e.Sgv <= 200);
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterByTypeAndSgv_WhenCombinedQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 80.0, type: "sgv"),
            CreateTestEntry(sgv: 120.0, type: "sgv"),
            CreateTestEntry(sgv: 180.0, type: "sgv"),
            CreateTestEntry(sgv: 120.0, type: "mbg"), // Same sgv but different type
        };
        await repository.CreateEntriesAsync(entries);

        // Act - Filter sgv type with sgv >= 100
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"type\":\"sgv\",\"sgv\":{\"$gte\":100}}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Type == "sgv" && e.Sgv >= 100);
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterWithAndOperator_WhenAndQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 90.0, type: "sgv", device: "dexcom"),
            CreateTestEntry(sgv: 120.0, type: "sgv", device: "dexcom"),
            CreateTestEntry(sgv: 150.0, type: "sgv", device: "libre"),
            CreateTestEntry(sgv: 120.0, type: "mbg", device: "dexcom"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - $and: type=sgv AND sgv>=100
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"$and\":[{\"type\":\"sgv\"},{\"sgv\":{\"$gte\":100}}]}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Type == "sgv" && e.Sgv >= 100);
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterWithOrOperator_WhenOrQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 110.0, type: "mbg"),
            CreateTestEntry(sgv: 120.0, type: "cal"),
            CreateTestEntry(sgv: 130.0, type: "rawbg"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - $or: type=sgv OR type=mbg
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"$or\":[{\"type\":\"sgv\"},{\"type\":\"mbg\"}]}"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Type == "sgv" || e.Type == "mbg");
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterWithNestedLogical_WhenComplexQueryProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 80.0, type: "sgv", device: "dexcom"),
            CreateTestEntry(sgv: 120.0, type: "sgv", device: "dexcom"),
            CreateTestEntry(sgv: 150.0, type: "sgv", device: "libre"),
            CreateTestEntry(sgv: 120.0, type: "mbg", device: "finger"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - $and with nested $or: (type=sgv) AND (device=dexcom OR device=libre)
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"$and\":[{\"type\":\"sgv\"},{\"$or\":[{\"device\":\"dexcom\"},{\"device\":\"libre\"}]}]}"
        );

        // Assert
        result.Should().HaveCount(3);
        result
            .Should()
            .OnlyContain(e => e.Type == "sgv" && (e.Device == "dexcom" || e.Device == "libre"));
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldFilterWithUrlEncodedQuery_WhenUrlFormatProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 80.0, type: "sgv"),
            CreateTestEntry(sgv: 120.0, type: "sgv"),
            CreateTestEntry(sgv: 180.0, type: "sgv"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - URL-encoded format: find[type]=sgv&find[sgv][$gte]=100
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "find[type]=sgv&find[sgv][$gte]=100"
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Type == "sgv" && e.Sgv >= 100);
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldReturnEmpty_WhenNoMatchingEntries()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 110.0, type: "sgv"),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - Query for non-existent type
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"type\":\"nonexistent\"}"
        );

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldCombineWithOtherParameters_WhenMultipleFiltersProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv", mills: now),
            CreateTestEntry(sgv: 120.0, type: "sgv", mills: now - 60000),
            CreateTestEntry(sgv: 140.0, type: "sgv", mills: now - 120000),
            CreateTestEntry(sgv: 160.0, type: "sgv", mills: now - 180000),
        };
        await repository.CreateEntriesAsync(entries);

        // Act - Filter by sgv and limit count
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            findQuery: "{\"sgv\":{\"$gte\":110}}",
            count: 2
        );

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Sgv >= 110);
    }

    #endregion


    [Fact]
    public async Task CountEntriesAsync_ShouldReturnCorrectCount_WhenEntriesExist()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 110.0, type: "sgv"),
            CreateTestEntry(sgv: 120.0, type: "mbg"),
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var totalCount = await repository.CountEntriesAsync();
        var sgvCount = await repository.CountEntriesAsync(type: "sgv");

        // Assert
        totalCount.Should().Be(3);
        sgvCount.Should().Be(2);
    }

    [Fact]
    public async Task CountEntriesAsync_ShouldReturnZero_WhenNoEntriesExist()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        // Act
        var count = await repository.CountEntriesAsync();

        // Assert
        count.Should().Be(0);
    }

    #endregion

    #region Bulk Operations Tests

    [Fact]
    public async Task DeleteEntriesAsync_ShouldDeleteAllEntries_WhenNoTypeSpecified()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 110.0, type: "mbg"),
            CreateTestEntry(sgv: 120.0, type: "cal"),
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var deletedCount = await repository.DeleteEntriesAsync();

        // Assert
        deletedCount.Should().Be(3);

        var remainingCount = await repository.CountEntriesAsync();
        remainingCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteEntriesAsync_ShouldDeleteOnlySpecifiedType_WhenTypeProvided()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: 100.0, type: "sgv"),
            CreateTestEntry(sgv: 110.0, type: "sgv"),
            CreateTestEntry(sgv: 120.0, type: "mbg"),
        };

        await repository.CreateEntriesAsync(entries);

        // Act
        var deletedCount = await repository.DeleteEntriesAsync(type: "sgv");

        // Assert
        deletedCount.Should().Be(2);

        var remainingCount = await repository.CountEntriesAsync();
        remainingCount.Should().Be(1);

        var remainingEntries = await repository.GetEntriesAsync();
        remainingEntries.First().Type.Should().Be("mbg");
    }

    #endregion

    #region Edge Cases and Error Handling Tests

    [Fact]
    public async Task CreateEntriesAsync_ShouldHandleEmptyCollection_Gracefully()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        // Act
        var result = await repository.CreateEntriesAsync(Array.Empty<Entry>());

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateEntriesAsync_ShouldHandleEntriesWithNullValues_Appropriately()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 120.0);
        entry.Direction = null;
        entry.Device = null;
        entry.Delta = null;
        entry.Notes = null;

        // Act
        var result = await repository.CreateEntriesAsync(new[] { entry });

        // Assert
        result.Should().HaveCount(1);
        var createdEntry = result.First();
        createdEntry.Direction.Should().BeNull();
        createdEntry.Device.Should().BeNull();
        createdEntry.Delta.Should().BeNull();
        createdEntry.Notes.Should().BeNull();
    }

    [Fact]
    public async Task CreateEntriesAsync_ShouldHandleEntriesWithExtremValues_Appropriately()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = new[]
        {
            CreateTestEntry(sgv: double.MaxValue), // Extreme high value
            CreateTestEntry(sgv: 0.0), // Zero value
            CreateTestEntry(sgv: 0.001), // Very small positive value
            CreateTestEntry(sgv: 999.999), // High but realistic value
        };

        // Act
        var result = await repository.CreateEntriesAsync(entries);

        // Assert
        result.Should().HaveCount(4);
        result.Select(e => e.Mgdl).Should().Contain(new[] { double.MaxValue, 0.0, 0.001, 999.999 });
    }

    [Fact]
    public async Task GetEntriesWithAdvancedFilterAsync_ShouldHandleInvalidDateString_Gracefully()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entry = CreateTestEntry(sgv: 120.0);
        await repository.CreateEntriesAsync(new[] { entry });

        // Act - Should not throw with invalid date
        var result = await repository.GetEntriesWithAdvancedFilterAsync(
            dateString: "invalid-date-string"
        );

        // Assert
        result.Should().HaveCount(1); // Should return all entries when date parsing fails
    }

    [Theory]
    [InlineData(0, 10)] // Normal pagination
    [InlineData(5, 0)] // Zero count
    [InlineData(0, 1000)] // Large count
    [InlineData(100, 10)] // Skip beyond available data
    public async Task GetEntriesAsync_ShouldHandleVariousPaginationValues_Appropriately(
        int skip,
        int count
    )
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var entries = Enumerable
            .Range(1, 20)
            .Select(i => CreateTestEntry(sgv: 100.0 + i))
            .ToArray();

        await repository.CreateEntriesAsync(entries);

        // Act
        var result = await repository.GetEntriesAsync(count: count, skip: skip);

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
    public async Task GetEntriesAsync_ShouldPerformWell_WithLargeDataset()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var baseTime = DateTimeOffset.UtcNow;
        var largeEntrySet = Enumerable
            .Range(1, 1000)
            .Select(i =>
                CreateTestEntry(
                    sgv: 100.0 + (i % 200), // Vary SGV values
                    type: i % 3 == 0 ? "mbg" : "sgv", // Mix of types
                    mills: baseTime.AddMinutes(-i).ToUnixTimeMilliseconds()
                )
            )
            .ToArray();

        await repository.CreateEntriesAsync(largeEntrySet);

        // Act & Assert - Should complete in reasonable time
        var start = DateTimeOffset.UtcNow;

        var pagedResults = await repository.GetEntriesAsync(count: 50, skip: 0);
        var filteredResults = await repository.GetEntriesAsync(type: "sgv", count: 100, skip: 0);
        var countResult = await repository.CountEntriesAsync();

        var elapsed = DateTimeOffset.UtcNow - start;

        // Assert results
        pagedResults.Should().HaveCount(50);
        filteredResults.Should().HaveCount(100);
        filteredResults.All(e => e.Type == "sgv").Should().BeTrue();
        countResult.Should().Be(1000);

        // Performance assertion - should complete within reasonable time
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CreateEntriesAsync_ShouldHandleBatchInsert_Efficiently()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var queryParser = new Nocturne.Infrastructure.Data.Services.QueryParser();
        var repository = new EntryRepository(
            context,
            queryParser,
            _mockDeduplicationService.Object,
            _mockLogger.Object
        );

        var batchSize = 100;
        var entries = Enumerable
            .Range(1, batchSize)
            .Select(i => CreateTestEntry(sgv: 100.0 + i))
            .ToArray();

        // Act
        var start = DateTimeOffset.UtcNow;
        var result = await repository.CreateEntriesAsync(entries);
        var elapsed = DateTimeOffset.UtcNow - start;

        // Assert
        result.Should().HaveCount(batchSize);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2)); // Should be reasonably fast
    }

    #endregion

    #region Test Helper Methods

    private static Entry CreateTestEntry(
        double sgv = 120.0,
        string type = "sgv",
        long? mills = null,
        string? id = null,
        string? direction = "Flat",
        string? device = null
    )
    {
        var timestamp = mills ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Entry
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Mills = timestamp,
            DateString = DateTimeOffset
                .FromUnixTimeMilliseconds(timestamp)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mgdl = sgv,
            Sgv = sgv,
            Direction = direction,
            Type = type,
            Device = device ?? "test-device",
            Delta = 0.0,
            Rssi = 100,
            Noise = 1,
        };
    }

    #endregion

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
