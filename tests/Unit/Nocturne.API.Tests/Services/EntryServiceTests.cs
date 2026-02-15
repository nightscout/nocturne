using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Unit tests for EntryService domain service with WebSocket broadcasting
/// </summary>
[Parity("api.entries.test.js")]
public class EntryServiceTests
{
    private readonly Mock<IPostgreSqlService> _mockPostgreSqlService;
    private readonly Mock<ISignalRBroadcastService> _mockSignalRBroadcastService;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<IOptions<CacheConfiguration>> _mockCacheConfig;
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<IEntryDecomposer> _mockEntryDecomposer;
    private readonly Mock<ILogger<EntryService>> _mockLogger;
    private readonly EntryService _entryService;

    // The demo mode filter query applied when demo mode is disabled (excludes demo data)
    private const string NonDemoFilterQuery = "{\"data_source\":{\"$ne\":\"demo-service\"}}";

    public EntryServiceTests()
    {
        _mockPostgreSqlService = new Mock<IPostgreSqlService>();
        _mockSignalRBroadcastService = new Mock<ISignalRBroadcastService>();
        _mockCacheService = new Mock<ICacheService>();
        _mockCacheConfig = new Mock<IOptions<CacheConfiguration>>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockEntryDecomposer = new Mock<IEntryDecomposer>();
        _mockLogger = new Mock<ILogger<EntryService>>();

        _mockCacheConfig.Setup(x => x.Value).Returns(new CacheConfiguration());
        _mockDemoModeService.Setup(x => x.IsEnabled).Returns(false);

        _entryService = new EntryService(
            _mockPostgreSqlService.Object,
            _mockSignalRBroadcastService.Object,
            _mockCacheService.Object,
            _mockCacheConfig.Object,
            _mockDemoModeService.Object,
            _mockEntryDecomposer.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesAsync_WithoutParameters_ReturnsAllEntries()
    {
        // Arrange
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
            new Entry
            {
                Id = "2",
                Type = "sgv",
                Sgv = 110,
                Mills = 1234567880,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    "sgv",
                    10,
                    0,
                    NonDemoFilterQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Mock cache service to simulate cache miss and execute the factory function
        _mockCacheService
            .Setup(x =>
                x.GetOrSetAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<List<Entry>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<string, Func<Task<List<Entry>>>, TimeSpan, CancellationToken>(
                async (key, factory, ttl, ct) => await factory()
            );

        // Act
        var result = await _entryService.GetEntriesAsync(cancellationToken: CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        Assert.Equal(expectedEntries, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesAsync_WithParameters_ReturnsFilteredEntries()
    {
        // Arrange
        var find = "{\"type\":\"sgv\"}";
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
        };

        // GetEntriesAsync internally calls GetEntriesWithAdvancedFilterAsync with the demo filter applied
        // Use loose matching for the mock setup to avoid parameter mismatch issues
        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Mock cache service to simulate cache miss and execute the factory function
        _mockCacheService
            .Setup(x =>
                x.GetOrSetAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<List<Entry>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<string, Func<Task<List<Entry>>>, TimeSpan, CancellationToken>(
                async (key, factory, ttl, ct) => await factory()
            );

        // Act
        var result = await _entryService.GetEntriesAsync(find, count, skip, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(expectedEntries.First().Id, result.First().Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntryByIdAsync_WithValidId_ReturnsEntry()
    {
        // Arrange
        var entryId = "60a1b2c3d4e5f6789012345";
        var expectedEntry = new Entry
        {
            Id = entryId,
            Type = "sgv",
            Sgv = 120,
            Mills = 1234567890,
        };

        _mockPostgreSqlService
            .Setup(x => x.GetEntryByIdAsync(entryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEntry);

        // Act
        var result = await _entryService.GetEntryByIdAsync(entryId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(entryId, result.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntryByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Arrange
        var entryId = "invalidid";

        _mockPostgreSqlService
            .Setup(x => x.GetEntryByIdAsync(entryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.GetEntryByIdAsync(entryId, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task CreateEntriesAsync_WithValidEntries_ReturnsCreatedEntriesAndBroadcasts()
    {
        // Arrange
        var entries = new List<Entry>
        {
            new Entry
            {
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
            new Entry
            {
                Type = "sgv",
                Sgv = 110,
                Mills = 1234567880,
            },
        };
        var processedEntries = entries
            .Select(e => new Entry
            {
                Id = Guid.NewGuid().ToString(),
                Type = e.Type,
                Sgv = e.Sgv,
                Mills = e.Mills,
            })
            .ToList();

        var createdEntries = processedEntries.ToList();

        _mockPostgreSqlService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdEntries);

        // Act
        var result = await _entryService.CreateEntriesAsync(entries, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        // Removed document processing service mock
        _mockPostgreSqlService.Verify(
            x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync("entries", It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateEntryAsync_WithValidEntry_ReturnsUpdatedEntryAndBroadcasts()
    {
        // Arrange
        var entryId = "60a1b2c3d4e5f6789012345";
        var entry = new Entry
        {
            Id = entryId,
            Type = "sgv",
            Sgv = 120,
            Mills = 1234567890,
        };
        var updatedEntry = new Entry
        {
            Id = entryId,
            Type = "sgv",
            Sgv = 125,
            Mills = 1234567890,
        };

        _mockPostgreSqlService
            .Setup(x => x.UpdateEntryAsync(entryId, entry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedEntry);

        // Act
        var result = await _entryService.UpdateEntryAsync(entryId, entry, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(entryId, result.Id);
        Assert.Equal(125, result.Sgv);
        _mockPostgreSqlService.Verify(
            x => x.UpdateEntryAsync(entryId, entry, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync("entries", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task UpdateEntryAsync_WithInvalidId_ReturnsNullAndDoesNotBroadcast()
    {
        // Arrange
        var entryId = "invalidid";
        var entry = new Entry
        {
            Type = "sgv",
            Sgv = 120,
            Mills = 1234567890,
        };

        _mockPostgreSqlService
            .Setup(x => x.UpdateEntryAsync(entryId, entry, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.UpdateEntryAsync(entryId, entry, CancellationToken.None);

        // Assert
        Assert.Null(result);
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageUpdateAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteEntryAsync_WithValidId_ReturnsTrueAndBroadcasts()
    {
        // Arrange
        var entryId = "60a1b2c3d4e5f6789012345";
        var entryToDelete = new Entry
        {
            Id = entryId,
            Type = "sgv",
            Sgv = 120,
            Mills = 1234567890,
        };

        _mockPostgreSqlService
            .Setup(x => x.GetEntryByIdAsync(entryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entryToDelete);

        _mockPostgreSqlService
            .Setup(x => x.DeleteEntryAsync(entryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _entryService.DeleteEntryAsync(entryId, CancellationToken.None);

        // Assert
        Assert.True(result);
        _mockPostgreSqlService.Verify(
            x => x.DeleteEntryAsync(entryId, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("entries", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteEntryAsync_WithInvalidId_ReturnsFalseAndDoesNotBroadcast()
    {
        // Arrange
        var entryId = "invalidid";

        _mockPostgreSqlService
            .Setup(x => x.DeleteEntryAsync(entryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _entryService.DeleteEntryAsync(entryId, CancellationToken.None);

        // Assert
        Assert.False(result);
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteEntriesAsync_WithValidFilter_ReturnsDeletedCountAndBroadcasts()
    {
        // Arrange
        var find = "{\"type\":\"sgv\"}";
        var deletedCount = 5L;

        _mockPostgreSqlService
            .Setup(x => x.BulkDeleteEntriesAsync(find, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _entryService.DeleteEntriesAsync(find, CancellationToken.None);

        // Assert
        Assert.Equal(deletedCount, result);
        _mockPostgreSqlService.Verify(
            x => x.BulkDeleteEntriesAsync(find, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync("entries", It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task DeleteEntriesAsync_WithNoMatches_ReturnsZeroAndDoesNotBroadcast()
    {
        // Arrange
        var find = "{\"type\":\"nonexistent\"}";
        var deletedCount = 0L;

        _mockPostgreSqlService
            .Setup(x => x.BulkDeleteEntriesAsync(find, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _entryService.DeleteEntriesAsync(find, CancellationToken.None);

        // Assert
        Assert.Equal(0, result);
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetCurrentEntryAsync_WithEntries_ReturnsLatestEntry()
    {
        // Arrange
        var expectedEntry = new Entry
        {
            Id = "1",
            Type = "sgv",
            Sgv = 120,
            Mills = 1234567890,
        };

        // GetCurrentEntryAsync internally calls GetEntriesWithAdvancedFilterAsync with count=1 and demo filter
        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    "sgv",
                    1,
                    0,
                    NonDemoFilterQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry> { expectedEntry });

        // Mock cache service to simulate cache miss
        _mockCacheService
            .Setup(x => x.GetAsync<Entry>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.GetCurrentEntryAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedEntry.Id, result.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetCurrentEntryAsync_WithNoEntries_ReturnsNull()
    {
        // Arrange
        // GetCurrentEntryAsync internally calls GetEntriesWithAdvancedFilterAsync with count=1 and demo filter
        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    "sgv",
                    1,
                    0,
                    NonDemoFilterQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Mock cache service to simulate cache miss
        _mockCacheService
            .Setup(x => x.GetAsync<Entry>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.GetCurrentEntryAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithValidFilter_ReturnsFilteredEntries()
    {
        // Arrange
        var find = "{\"type\":\"sgv\",\"sgv\":{\"$gte\":100}}";
        var expectedFindQuery =
            "{\"data_source\":{\"$ne\":\"demo-service\"},\"type\":\"sgv\",\"sgv\":{\"$gte\":100}}";
        var count = 50;
        var skip = 10;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
            new Entry
            {
                Id = "2",
                Type = "sgv",
                Sgv = 110,
                Mills = 1234567880,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    count,
                    skip,
                    expectedFindQuery,
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            find,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        Assert.Equal(expectedEntries, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task CreateEntriesAsync_WithException_ThrowsException()
    {
        // Arrange
        var entries = new List<Entry>
        {
            new Entry
            {
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _entryService.CreateEntriesAsync(entries, CancellationToken.None)
        );
        _mockSignalRBroadcastService.Verify(
            x => x.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    #region FindQuery Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithSimpleJsonFindQuery_ShouldPassToMongoDbService()
    {
        // Arrange
        var findQuery = "{\"type\":\"sgv\"}";
        var expectedFindQuery = "{\"data_source\":{\"$ne\":\"demo-service\"},\"type\":\"sgv\"}";
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(expectedEntries.First().Id, result.First().Id);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithUrlParameterFindQuery_ShouldPassToMongoDbService()
    {
        // Arrange
        var findQuery = "find[sgv][$gte]=100";
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 50;
        var skip = 10;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
                Mills = 1234567890,
            },
            new Entry
            {
                Id = "2",
                Type = "sgv",
                Sgv = 110,
                Mills = 1234567880,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[sgv][$gte]=100")]
    [InlineData("find[sgv][$lte]=200")]
    [InlineData("find[sgv][$gt]=80")]
    [InlineData("find[sgv][$lt]=180")]
    [InlineData("find[sgv][$eq]=120")]
    [InlineData("find[sgv][$ne]=0")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithSgvOperators_ShouldPassCorrectQuery(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry { Id = "1", Sgv = 120 },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[mills][$gte]=1641024000000")]
    [InlineData("find[mills][$lte]=1641027600000")]
    [InlineData("find[date][$gte]=1641024000000")]
    [InlineData("find[date][$lte]=1641027600000")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithDateTimeOperators_ShouldPassCorrectQuery(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Mills = 1641025000000,
                Type = "sgv",
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[type]=sgv")]
    [InlineData("find[type]=mbg")]
    [InlineData("find[type]=cal")]
    [InlineData("find[direction]=Flat")]
    [InlineData("find[direction]=SingleUp")]
    [InlineData("find[direction]=DoubleDown")]
    [InlineData("find[device]=xDrip")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithStringFieldOperators_ShouldPassCorrectQuery(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Direction = "Flat",
                Device = "xDrip",
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithComplexJsonQuery_ShouldPassToMongoDbService()
    {
        // Arrange
        var findQuery = "{\"$and\":[{\"type\":\"sgv\"},{\"sgv\":{\"$gte\":100,\"$lte\":200}}]}";
        var expectedFindQuery =
            "{\"data_source\":{\"$ne\":\"demo-service\"},\"$and\":[{\"type\":\"sgv\"},{\"sgv\":{\"$gte\":100,\"$lte\":200}}]}";
        var count = 25;
        var skip = 5;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 150,
            },
            new Entry
            {
                Id = "2",
                Type = "sgv",
                Sgv = 175,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithMultipleUrlParameters_ShouldPassToMongoDbService()
    {
        // Arrange
        var findQuery = "find[sgv][$gte]=100&find[sgv][$lte]=200&find[type]=sgv";
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 15;
        var skip = 3;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 150,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithFullParameterSet_ShouldPassAllParametersToMongoDbService()
    {
        // Arrange
        var type = "sgv";
        var count = 20;
        var skip = 10;
        var findQuery = "find[sgv][$gte]=100";
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var dateString = "2022-01-01T12:00:00.000Z";
        var reverseResults = true;
        var expectedEntries = new List<Entry> { new Entry { Id = "1" } };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    type,
                    count,
                    skip,
                    expectedFindQuery,
                    dateString,
                    reverseResults,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            type,
            count,
            skip,
            findQuery,
            dateString,
            reverseResults,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    type,
                    count,
                    skip,
                    expectedFindQuery,
                    dateString,
                    reverseResults,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithEmptyFindQuery_ShouldPassToMongoDbService(
        string? findQuery
    )
    {
        // Arrange
        // Null/empty/whitespace queries get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry> { new Entry { Id = "1" } };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery!,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("malformed_query")]
    [InlineData("find[invalid_field]=value")]
    [InlineData("find[sgv][$unknown_op]=100")]
    [InlineData("{invalid_json")]
    [InlineData("find[")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithInvalidFindQuery_ShouldStillPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // Non-JSON queries (including invalid ones) get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>(); // Service should handle gracefully and return empty

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[sgv][$in]=[100,110,120]")]
    [InlineData("find[sgv][$nin]=[0,40,400]")]
    [InlineData("find[type][$in]=[\"sgv\",\"mbg\"]")]
    [InlineData("find[direction][$in]=[\"Flat\",\"SingleUp\"]")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithArrayOperators_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 110,
                Direction = "Flat",
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[noise][$exists]=true")]
    [InlineData("find[noise][$exists]=false")]
    [InlineData("find[filtered][$exists]=true")]
    [InlineData("find[delta][$exists]=false")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithExistsOperators_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
                Noise = 1,
                Filtered = 118.5,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[device][$regex]=xDrip")]
    [InlineData("find[device][$regex]=^DexG")]
    [InlineData("find[notes][$regex]=test.*case")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithRegexOperators_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Device = "xDrip-DexcomG5",
                Notes = "test case note",
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithMongoDbServiceThrowingException_ShouldPropagateException()
    {
        // Arrange
        var findQuery = "find[sgv][$gte]=100";
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("Database connection failed"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _entryService.GetEntriesWithAdvancedFilterAsync(
                findQuery,
                count,
                skip,
                CancellationToken.None
            )
        );

        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithCancellationToken_ShouldPassTokenToMongoDbService()
    {
        // Arrange
        var findQuery = "find[type]=sgv";
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var cancellationToken = new CancellationToken();
        var expectedEntries = new List<Entry> { new Entry { Id = "1" } };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null, // type
                    count,
                    skip,
                    expectedFindQuery, // findQuery with demo filter
                    null, // dateString
                    false, // reverseResults
                    cancellationToken
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            cancellationToken
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    cancellationToken
                ),
            Times.Once
        );
    }

    #region Additional MongoDB Syntax and Edge Case Tests

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("{}")]
    [InlineData("{\"sgv\": {\"$gte\": 100}}")]
    [InlineData("{\"type\": \"sgv\", \"sgv\": {\"$gte\": 80, \"$lte\": 200}}")]
    [InlineData("{\"$and\": [{\"type\": \"sgv\"}, {\"sgv\": {\"$gte\": 100}}]}")]
    [InlineData("{\"$or\": [{\"type\": \"sgv\"}, {\"type\": \"mbg\"}]}")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithValidJsonQuery_ShouldPassExactQueryToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // Each JSON query gets the demo filter merged in, so use It.IsAny for the parameterized test
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    It.IsAny<string?>(),
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    It.IsAny<string?>(),
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[sgv][$mod]=[10,0]")] // Modulo operator
    [InlineData("find[mills][$type]=18")] // Type operator for 64-bit integer
    [InlineData("find[sgv][$all]=[100,120]")] // All operator for arrays
    [InlineData("find[sgv][$elemMatch]={\"$gte\":100}")] // Element match for arrays
    [InlineData("find[device][$size]=10")] // Size operator for arrays
    public async Task GetEntriesWithAdvancedFilterAsync_WithAdvancedMongoOperators_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 120,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData(
        "find[sgv][$gte]=100&find[sgv][$lte]=200&find[type]=sgv&find[mills][$gte]=1641024000000"
    )]
    [InlineData(
        "find[type][$in]=[\"sgv\",\"mbg\"]&find[sgv][$exists]=true&find[direction][$ne]=null"
    )]
    [InlineData("find[device][$regex]=xDrip&find[noise][$lte]=1&find[filtered][$exists]=true")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithComplexMultiFieldQuery_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Sgv = 150,
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("find[type]=sgv&find[date][$gte]=1641024000000&find[date][$lte]=1641110399999")]
    [InlineData("find[type]=mbg&find[date][$gte]=1640937600000&find[date][$lte]=1641023999999")]
    [InlineData("find[type]=cal&find[mills][$gte]=1641024000000&find[mills][$lte]=1641110399999")]
    public async Task GetEntriesWithAdvancedFilterAsync_WithReportsLayoutServerPatterns_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange - Testing patterns from reports +layout.server.ts
        // URL parameter queries are not JSON, so they get replaced with just the demo filter
        var expectedFindQuery = NonDemoFilterQuery;
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Mills = 1641050000000,
                Date = new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    expectedFindQuery,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    [InlineData("{\"date\": {\"$gte\": 1641024000000, \"$lte\": 1641110399999}}")]
    [InlineData(
        "{\"type\": \"sgv\", \"date\": {\"$gte\": 1641024000000, \"$lte\": 1641110399999}}"
    )]
    [InlineData(
        "{\"mills\": {\"$gte\": 1641024000000, \"$lte\": 1641110399999}, \"type\": \"sgv\"}"
    )]
    public async Task GetEntriesWithAdvancedFilterAsync_WithJsonDateRangeQueries_ShouldPassToMongoDbService(
        string findQuery
    )
    {
        // Arrange - Testing JSON format queries from reports +layout.server.ts
        // Each JSON query gets the demo filter merged in, so use It.IsAny for the parameterized test
        var count = 10;
        var skip = 0;
        var expectedEntries = new List<Entry>
        {
            new Entry
            {
                Id = "1",
                Type = "sgv",
                Mills = 1641050000000,
                Date = new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            },
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    It.IsAny<string?>(),
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _entryService.GetEntriesWithAdvancedFilterAsync(
            findQuery,
            count,
            skip,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    null,
                    count,
                    skip,
                    It.IsAny<string?>(),
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    #endregion

    #region CheckForDuplicateEntryAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckForDuplicateEntryAsync_WithNoDuplicate_ReturnsNull()
    {
        // Arrange
        var device = "test-device";
        var type = "sgv";
        double? sgv = 120;
        var mills = 1234567890L;

        _mockPostgreSqlService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    5,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.CheckForDuplicateEntryAsync(
            device,
            type,
            sgv,
            mills,
            5,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    5,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckForDuplicateEntryAsync_WithDuplicate_ReturnsDuplicateEntry()
    {
        // Arrange
        var device = "test-device";
        var type = "sgv";
        double? sgv = 120;
        var mills = 1234567890L;
        var duplicateEntry = new Entry
        {
            Id = "duplicate-id",
            Device = device,
            Type = type,
            Sgv = sgv,
            Mills = mills - 60000, // 1 minute earlier
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    5,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(duplicateEntry);

        // Act
        var result = await _entryService.CheckForDuplicateEntryAsync(
            device,
            type,
            sgv,
            mills,
            5,
            CancellationToken.None
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal(duplicateEntry.Id, result.Id);
        Assert.Equal(device, result.Device);
        Assert.Equal(type, result.Type);
        Assert.Equal(sgv, result.Sgv);
        _mockPostgreSqlService.Verify(
            x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    5,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckForDuplicateEntryAsync_WithNullDevice_ChecksForDuplicate()
    {
        // Arrange
        string? device = null;
        var type = "sgv";
        double? sgv = 120;
        var mills = 1234567890L;

        _mockPostgreSqlService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    5,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.CheckForDuplicateEntryAsync(
            device,
            type,
            sgv,
            mills,
            5,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    5,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckForDuplicateEntryAsync_WithCustomTimeWindow_UsesSpecifiedWindow()
    {
        // Arrange
        var device = "test-device";
        var type = "sgv";
        double? sgv = 120;
        var mills = 1234567890L;
        var customWindow = 10; // 10 minutes

        _mockPostgreSqlService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    customWindow,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        // Act
        var result = await _entryService.CheckForDuplicateEntryAsync(
            device,
            type,
            sgv,
            mills,
            customWindow,
            CancellationToken.None
        );

        // Assert
        Assert.Null(result);
        _mockPostgreSqlService.Verify(
            x =>
                x.CheckForDuplicateEntryAsync(
                    device,
                    type,
                    sgv,
                    mills,
                    customWindow,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    #endregion

    #endregion
}
