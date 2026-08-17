using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// Unit tests for the DeduplicationService focusing on basal type deduplication.
/// When a Basal and Temp Basal occur at the same time, the deduplication service
/// should group them together and prefer Temp Basal as the merged type.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class DeduplicationServiceTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly ServiceProvider _serviceProvider;

    public DeduplicationServiceTests()
    {
        // Create in-memory SQLite database for testing
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        // Create the database schema and seed the tenant
        using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        context.Database.EnsureCreated();
        context.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
        context.SaveChanges();

        // Set up DI container for IServiceScopeFactory
        var services = new ServiceCollection();
        services.AddScoped(sp =>
        {
            var ctx = new NocturneDbContext(_contextOptions);
            ctx.TenantId = TestTenantId;
            return ctx;
        });
        services.AddScoped<IDeduplicationService, DeduplicationService>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();
    }

    #region StateSpan Deduplication Tests

    [Fact]
    public async Task DeduplicateAllAsync_ShouldDeduplicateStateSpansAcrossBucketBoundaries()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Create timestamps that straddle a 30-second bucket boundary
        // Bucket size is 30,000ms, so bucket boundaries are at multiples of 30,000
        var bucketBoundary = 30000L * 1000; // Timestamp at bucket boundary
        var glookoTimestamp = bucketBoundary - 5000; // 5 seconds before boundary (bucket 999)
        var mylifeTimestamp = bucketBoundary + 5000; // 5 seconds after boundary (bucket 1000)

        // These are only 10 seconds apart but in different buckets!
        // They should still be deduplicated because they're within the 30-second window

        var glookoStateSpan = CreateTestStateSpan(
            category: StateSpanCategory.PumpMode,
            state: "Active",
            startMills: glookoTimestamp,
            source: "glooko-connector"
        );

        var mylifeStateSpan = CreateTestStateSpan(
            category: StateSpanCategory.PumpMode,
            state: "Active",
            startMills: mylifeTimestamp,
            source: "mylife-connector"
        );

        context.StateSpans.AddRange(
            StateSpanMapper.ToEntity(glookoStateSpan),
            StateSpanMapper.ToEntity(mylifeStateSpan)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.StateSpansProcessed.Should().Be(2);

        // Both should be grouped together despite being in different buckets
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "statespan")
            .OrderBy(lr => lr.SourceTimestamp)
            .ToListAsync();

        linkedRecords.Should().HaveCount(2, "both state spans should be linked");
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "both state spans should share the same canonical ID because they are within 30 seconds and have the same category/state");

        // Verify the sources are different
        linkedRecords.Select(lr => lr.DataSource).Should().BeEquivalentTo(
            new[] { "glooko-connector", "mylife-connector" },
            "the two linked records should be from different sources");

        // Verify we can get a unified state span
        var canonicalId = linkedRecords.First().CanonicalId;
        var unified = await service.GetUnifiedStateSpanAsync(canonicalId);
        unified.Should().NotBeNull();
        unified!.Sources.Should().BeEquivalentTo(new[] { "glooko-connector", "mylife-connector" });
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldNotDeduplicateStateSpansWithDifferentStates()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var stateSpan1 = CreateTestStateSpan(
            category: StateSpanCategory.PumpMode,
            state: "Active",
            startMills: timestamp,
            source: "glooko-connector"
        );

        var stateSpan2 = CreateTestStateSpan(
            category: StateSpanCategory.PumpMode,
            state: "Suspended",  // Different state
            startMills: timestamp + 1000,
            source: "mylife-connector"
        );

        context.StateSpans.AddRange(
            StateSpanMapper.ToEntity(stateSpan1),
            StateSpanMapper.ToEntity(stateSpan2)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.StateSpansProcessed.Should().Be(2);

        // They should NOT be grouped because they have different states
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "statespan")
            .ToListAsync();

        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2,
            "state spans with different states should not be grouped together");
    }

    #endregion

    #region TempBasal Deduplication Tests

    [Fact]
    public async Task DeduplicateAllAsync_ShouldGroupTempBasals_FromDifferentConnectors()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var timestamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Simulate Glooko and MyLife writing the same basal event
        var glookoTempBasal = CreateTestTempBasalEntity(
            startTimestamp: timestamp,
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "glooko-connector",
            legacyId: "glooko_scheduledbasal_123"
        );
        var mylifeTempBasal = CreateTestTempBasalEntity(
            startTimestamp: timestamp.AddSeconds(2), // 2 seconds later
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "mylife-connector",
            legacyId: "mylife_basal_456"
        );

        context.TempBasals.AddRange(glookoTempBasal, mylifeTempBasal);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TempBasalsProcessed.Should().Be(2);

        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "both temp basals should share the same canonical ID");
        linkedRecords.Select(lr => lr.DataSource).Should().BeEquivalentTo(
            new[] { "glooko-connector", "mylife-connector" });

        result.DuplicateGroupsFound.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldNotGroupTempBasals_WithDifferentRates()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var timestamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var tempBasal1 = CreateTestTempBasalEntity(
            startTimestamp: timestamp,
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "glooko-connector"
        );
        var tempBasal2 = CreateTestTempBasalEntity(
            startTimestamp: timestamp.AddSeconds(5),
            rate: 0.8, // Different rate
            origin: "Scheduled",
            dataSource: "mylife-connector"
        );

        context.TempBasals.AddRange(tempBasal1, tempBasal2);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TempBasalsProcessed.Should().Be(2);

        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2,
            "temp basals with different rates should not be grouped");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldGroupTempBasals_WithDifferentOrigins()
    {
        // Arrange — origin should NOT prevent cross-connector deduplication
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var timestamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var scheduledBasal = CreateTestTempBasalEntity(
            startTimestamp: timestamp,
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "glooko-connector"
        );
        var algorithmBasal = CreateTestTempBasalEntity(
            startTimestamp: timestamp.AddSeconds(5),
            rate: 1.2, // Same rate
            origin: "Algorithm", // Different origin — should still group
            dataSource: "mylife-connector"
        );

        context.TempBasals.AddRange(scheduledBasal, algorithmBasal);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();

        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "temp basals with different origins but same rate should be grouped for cross-connector dedup");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldNotGroupTempBasals_OutsideTimeWindow()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var timestamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var tempBasal1 = CreateTestTempBasalEntity(
            startTimestamp: timestamp,
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "glooko-connector"
        );
        var tempBasal2 = CreateTestTempBasalEntity(
            startTimestamp: timestamp.AddMinutes(15), // well outside the 10-minute wide window
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "mylife-connector"
        );

        context.TempBasals.AddRange(tempBasal1, tempBasal2);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();

        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2,
            "temp basals outside the time window should not be grouped");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldHandleSingleTempBasalEntity_WithoutError()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var tempBasal = CreateTestTempBasalEntity(
            startTimestamp: new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
            rate: 1.2,
            origin: "Scheduled",
            dataSource: "glooko-connector"
        );

        context.TempBasals.Add(tempBasal);
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TempBasalsProcessed.Should().Be(1);
        // Single record should not be a duplicate group
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "tempbasal")
            .ToListAsync();
        linkedRecords.Should().HaveCount(1);
    }

    #endregion

    #region Batch Deduplication Tests

    [Fact]
    public async Task DeduplicateBatchAsync_LinksAllRecordsWithDistinctTimestamps()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var tb1 = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.0, origin: "Scheduled", dataSource: "test-connector");
        var tb2 = CreateTestTempBasalEntity(startTimestamp: baseTime.AddMinutes(2), rate: 1.5, origin: "Scheduled", dataSource: "test-connector");
        var tb3 = CreateTestTempBasalEntity(startTimestamp: baseTime.AddMinutes(4), rate: 2.0, origin: "Scheduled", dataSource: "test-connector");

        context.TempBasals.AddRange(tb1, tb2, tb3);
        await context.SaveChangesAsync();

        var inputs = new List<DeduplicationInput>
        {
            ToDeduplicationInput(tb1),
            ToDeduplicationInput(tb2),
            ToDeduplicationInput(tb3)
        };

        // Act
        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, inputs);

        // Assert
        result.Processed.Should().Be(3);
        result.GroupsCreated.Should().Be(3);
        result.RecordsLinked.Should().Be(3);

        var linkedRecords = await context.LinkedRecords.ToListAsync();
        linkedRecords.Should().HaveCount(3);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_GroupsDuplicatesWithinTimeWindow()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var tb1 = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.5, origin: "Scheduled", dataSource: "glooko-connector");
        var tb2 = CreateTestTempBasalEntity(startTimestamp: baseTime.AddSeconds(5), rate: 1.5, origin: "Scheduled", dataSource: "mylife-connector");

        context.TempBasals.AddRange(tb1, tb2);
        await context.SaveChangesAsync();

        var inputs = new List<DeduplicationInput>
        {
            ToDeduplicationInput(tb1),
            ToDeduplicationInput(tb2)
        };

        // Act
        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, inputs);

        // Assert
        result.DuplicateGroups.Should().BeGreaterThanOrEqualTo(1);

        var linkedRecords = await context.LinkedRecords.ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "both temp basals should share the same canonical ID");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_ChunksLargeBatch_AndMergesAcrossChunkBoundary()
    {
        // A connector backfill can hand the dedup pass thousands of records spanning a wide
        // time window. DeduplicateBatchAsync sorts by event time and slices into DedupChunkSize
        // (500) chunks so each matching-window query stays narrow. This guards two things:
        // (1) the whole batch is processed across chunks, and (2) a duplicate pair that lands on
        // opposite sides of a chunk boundary still merges — the later chunk's window query
        // re-reads the earlier chunk's freshly-written link.
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // 500 distinct records, 1 minute apart (well outside the 30s window), distinct rates.
        // After sorting these occupy the first chunk (indices 0..499); the last of them is the
        // partner for the boundary-straddling duplicate below.
        var entities = new List<TempBasalEntity>();
        for (var i = 0; i < 500; i++)
        {
            entities.Add(CreateTestTempBasalEntity(
                startTimestamp: baseTime.AddMinutes(i),
                rate: 1.0 + i, // 1.0 spacing >> 0.05 tolerance, so no accidental matches
                origin: "Scheduled",
                dataSource: "glooko-connector"));
        }

        // Duplicate of the 500th record (same rate, 5s later) from another connector. Sorted by
        // time it lands at index 500 — the first record of the second chunk — so the matching
        // partner sits in the previous chunk.
        var partner = entities[499];
        var boundaryDuplicate = CreateTestTempBasalEntity(
            startTimestamp: partner.StartTimestamp.AddSeconds(5),
            rate: partner.Rate,
            origin: "Scheduled",
            dataSource: "mylife-connector");
        entities.Add(boundaryDuplicate);

        context.TempBasals.AddRange(entities);
        await context.SaveChangesAsync();

        // Feed inputs with the boundary duplicate out of time order to prove the sort, not the
        // input order, is what places records into chunks.
        var inputs = entities.Select(ToDeduplicationInput).ToList();

        // Act
        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, inputs);

        // Assert
        result.Processed.Should().Be(501);
        result.RecordsLinked.Should().Be(501, "every record links even when the batch spans multiple chunks");

        var linkedRecords = await context.LinkedRecords.ToListAsync();
        linkedRecords.Should().HaveCount(501);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(500,
            "the cross-chunk duplicate pair collapses into a single canonical group");

        var partnerCanonical = linkedRecords.Single(lr => lr.RecordId == partner.Id).CanonicalId;
        var duplicateCanonical = linkedRecords.Single(lr => lr.RecordId == boundaryDuplicate.Id).CanonicalId;
        duplicateCanonical.Should().Be(partnerCanonical,
            "a duplicate straddling a chunk boundary must still share its partner's canonical ID");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_SkipsAlreadyLinkedRecords()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var tb = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.2, origin: "Scheduled", dataSource: "test-connector");

        context.TempBasals.Add(tb);
        await context.SaveChangesAsync();

        // Manually add a linked record for it
        context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            CanonicalId = Guid.CreateVersion7(),
            RecordType = "tempbasal",
            RecordId = tb.Id,
            SourceTimestamp = new DateTimeOffset(tb.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DataSource = tb.DataSource ?? DeduplicationInput.UnknownDataSource,
            IsPrimary = true,
            SysCreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var inputs = new List<DeduplicationInput> { ToDeduplicationInput(tb) };

        // Act
        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, inputs);

        // Assert
        result.RecordsLinked.Should().Be(0, "no new links should be created for already-linked records");

        var linkedRecords = await context.LinkedRecords.ToListAsync();
        linkedRecords.Should().HaveCount(1, "still only the original linked record");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_HandlesIntraBatchDedup()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Same timestamp and same rate — duplicates within the batch
        var tb1 = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.5, origin: "Scheduled", dataSource: "glooko-connector");
        var tb2 = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.5, origin: "Scheduled", dataSource: "mylife-connector");

        context.TempBasals.AddRange(tb1, tb2);
        await context.SaveChangesAsync();

        var inputs = new List<DeduplicationInput>
        {
            ToDeduplicationInput(tb1),
            ToDeduplicationInput(tb2)
        };

        // Act
        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, inputs);

        // Assert
        var linkedRecords = await context.LinkedRecords.ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "both records should share the same canonical ID");
        linkedRecords.Count(lr => lr.IsPrimary).Should().Be(1,
            "exactly one record should be marked as primary");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_MatchesExistingCanonicalGroups()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // First record — creates a canonical group
        var tbA = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.5, origin: "Scheduled", dataSource: "glooko-connector");
        context.TempBasals.Add(tbA);
        await context.SaveChangesAsync();

        var inputsA = new List<DeduplicationInput> { ToDeduplicationInput(tbA) };
        await service.DeduplicateBatchAsync(RecordType.TempBasal, inputsA);

        // Second record — within 30s of A, same rate, should match A's canonical group
        var tbB = CreateTestTempBasalEntity(startTimestamp: baseTime.AddSeconds(10), rate: 1.5, origin: "Scheduled", dataSource: "mylife-connector");
        context.TempBasals.Add(tbB);
        await context.SaveChangesAsync();

        var inputsB = new List<DeduplicationInput> { ToDeduplicationInput(tbB) };

        // Act
        await service.DeduplicateBatchAsync(RecordType.TempBasal, inputsB);

        // Assert
        var linkedRecords = await context.LinkedRecords.ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "B should be linked to A's existing canonical group");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_DoesNotPromoteToPrimary_WhenCanonicalAlreadyExists()
    {
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var tbA = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.5, origin: "Scheduled", dataSource: "glooko-connector");
        context.TempBasals.Add(tbA);
        await context.SaveChangesAsync();
        await service.DeduplicateBatchAsync(RecordType.TempBasal, new List<DeduplicationInput> { ToDeduplicationInput(tbA) });

        var tbB = CreateTestTempBasalEntity(startTimestamp: baseTime.AddSeconds(10), rate: 1.5, origin: "Scheduled", dataSource: "mylife-connector");
        context.TempBasals.Add(tbB);
        await context.SaveChangesAsync();
        await service.DeduplicateBatchAsync(RecordType.TempBasal, new List<DeduplicationInput> { ToDeduplicationInput(tbB) });

        var linked = await context.LinkedRecords.OrderBy(lr => lr.SourceTimestamp).ToListAsync();
        linked.Should().HaveCount(2);
        linked[0].RecordId.Should().Be(tbA.Id);
        linked[0].IsPrimary.Should().BeTrue("A is the only member when first inserted");
        linked[1].RecordId.Should().Be(tbB.Id);
        linked[1].IsPrimary.Should().BeFalse("primary is sticky — B joining A's existing canonical does not promote B");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_HandlesMixedBatch_NewExistingAndIntraBatch()
    {
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Seed: pre-existing canonical group via prior batch
        var existing = CreateTestTempBasalEntity(startTimestamp: baseTime, rate: 1.5, origin: "Scheduled", dataSource: "glooko-connector");
        context.TempBasals.Add(existing);
        await context.SaveChangesAsync();
        await service.DeduplicateBatchAsync(RecordType.TempBasal, new List<DeduplicationInput> { ToDeduplicationInput(existing) });

        // New batch:
        //   tbMatchExisting: matches existing canonical → joins it, IsPrimary=false
        //   tbNew1 + tbNew2: same time + rate → intra-batch dedup → one canonical, one primary
        //   tbStandalone: distinct time + rate → new canonical, IsPrimary=true
        var tbMatchExisting = CreateTestTempBasalEntity(startTimestamp: baseTime.AddSeconds(5), rate: 1.5, origin: "Scheduled", dataSource: "mylife-connector");
        var tbNew1 = CreateTestTempBasalEntity(startTimestamp: baseTime.AddMinutes(10), rate: 2.0, origin: "Scheduled", dataSource: "glooko-connector");
        var tbNew2 = CreateTestTempBasalEntity(startTimestamp: baseTime.AddMinutes(10).AddSeconds(2), rate: 2.0, origin: "Scheduled", dataSource: "mylife-connector");
        var tbStandalone = CreateTestTempBasalEntity(startTimestamp: baseTime.AddMinutes(20), rate: 0.5, origin: "Scheduled", dataSource: "glooko-connector");

        context.TempBasals.AddRange(tbMatchExisting, tbNew1, tbNew2, tbStandalone);
        await context.SaveChangesAsync();

        var inputs = new List<DeduplicationInput>
        {
            ToDeduplicationInput(tbMatchExisting),
            ToDeduplicationInput(tbNew1),
            ToDeduplicationInput(tbNew2),
            ToDeduplicationInput(tbStandalone),
        };

        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, inputs);

        result.Processed.Should().Be(4);
        result.RecordsLinked.Should().Be(4);
        result.GroupsCreated.Should().Be(2, "tbNew1+tbNew2 share one new canonical; tbStandalone is another new canonical");

        var linked = await context.LinkedRecords.ToListAsync();
        linked.Should().HaveCount(5, "1 seed + 4 new");

        var canonicalCounts = linked.GroupBy(lr => lr.CanonicalId).ToDictionary(g => g.Key, g => g.ToList());
        canonicalCounts.Should().HaveCount(3, "existing + intra-batch group + standalone");

        foreach (var (canonicalId, members) in canonicalCounts)
        {
            members.Count(m => m.IsPrimary).Should().Be(1, $"canonical {canonicalId} should have exactly one primary");
        }

        var matchExistingLink = linked.Single(lr => lr.RecordId == tbMatchExisting.Id);
        matchExistingLink.IsPrimary.Should().BeFalse("joining existing canonical never promotes");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_ReturnsEmptyResultForEmptyBatch()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        context.TenantId = TestTenantId;
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Act
        var result = await service.DeduplicateBatchAsync(RecordType.TempBasal, new List<DeduplicationInput>());

        // Assert
        result.Processed.Should().Be(0);
        result.GroupsCreated.Should().Be(0);
        result.RecordsLinked.Should().Be(0);
        result.DuplicateGroups.Should().Be(0);
    }

    #endregion

    #region Wide Matching Window Tests

    [Fact]
    public async Task DeduplicateBatchAsync_MatchesCrossSourceBolus_PastTheTightWindow()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // Two connectors reporting the same pump whose clocks have drifted 64 seconds apart.
        var mylife = CreateBolus(WideBase, 2.0, "mylife-connector");
        var glooko = CreateBolus(WideBase + 64_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(mylife, glooko);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylife)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(2);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "the same dose reported by two connectors is one event even after the clocks drift apart");
        links.Count(lr => lr.IsPrimary).Should().Be(1, "exactly one record in the group is primary");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_MatchesCrossSourceDeviceEvent_PastTheTightWindow()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var mylife = CreateDeviceEvent(WideBase, "SiteChange", "mylife-connector");
        var glooko = CreateDeviceEvent(WideBase + 64_000, "SiteChange", "glooko-connector");
        context.DeviceEvents.AddRange(mylife, glooko);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.DeviceEvent, [ToInput(mylife)]);
        await service.DeduplicateBatchAsync(RecordType.DeviceEvent, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(2);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_MatchesCrossSourceCarbIntake_PastTheTightWindow()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var mylife = CreateCarbIntake(WideBase, 45, "mylife-connector");
        var glooko = CreateCarbIntake(WideBase + 64_000, 45, "glooko-connector");
        context.CarbIntakes.AddRange(mylife, glooko);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.CarbIntake, [ToInput(mylife)]);
        await service.DeduplicateBatchAsync(RecordType.CarbIntake, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(2);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1);
    }

    [Theory]
    [InlineData(30_000, false, 1)]
    [InlineData(30_001, false, 2)]
    [InlineData(30_000, true, 1)]
    [InlineData(30_001, true, 2)]
    public async Task DeduplicateBatchAsync_PinsTightWindowEdge(long offsetMillis, bool laterFirst, int expectedGroups)
    {
        // Both records carry the same source, so the wide window can never rescue the just-past
        // case: only the tight window's inclusive bound decides the outcome. Separate batches so
        // the second record matches through the persisted link rather than intra-batch state.
        // laterFirst flips which end of the window the second record has to reach across.
        await using var context = NewContext();
        var service = CreateService(context);

        var earlier = CreateBolus(WideBase, 2.0, "mylife-connector");
        var later = CreateBolus(WideBase + offsetMillis, 2.0, "mylife-connector");
        context.Boluses.AddRange(earlier, later);
        await context.SaveChangesAsync();

        var (first, second) = laterFirst ? (later, earlier) : (earlier, later);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(first)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(second)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(expectedGroups);
    }

    [Theory]
    [InlineData(600_000, false, 1)]
    [InlineData(600_001, false, 2)]
    [InlineData(600_000, true, 1)]
    [InlineData(600_001, true, 2)]
    public async Task DeduplicateBatchAsync_PinsWideWindowEdge_AcrossBatches(long offsetMillis, bool laterFirst, int expectedGroups)
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var earlier = CreateBolus(WideBase, 2.0, "mylife-connector");
        var later = CreateBolus(WideBase + offsetMillis, 2.0, "glooko-connector");
        context.Boluses.AddRange(earlier, later);
        await context.SaveChangesAsync();

        var (first, second) = laterFirst ? (later, earlier) : (earlier, later);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(first)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(second)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(expectedGroups);
    }

    [Theory]
    [InlineData(600_000, 1)]
    [InlineData(600_001, 2)]
    public async Task DeduplicateBatchAsync_PinsWideWindowEdge_WithinOneBatch(long offsetMillis, int expectedGroups)
    {
        // The intra-batch arm of the wide path: the second record matches a canonical minted
        // earlier in the same batch rather than a persisted link.
        await using var context = NewContext();
        var service = CreateService(context);

        var mylife = CreateBolus(WideBase, 2.0, "mylife-connector");
        var glooko = CreateBolus(WideBase + offsetMillis, 2.0, "glooko-connector");
        context.Boluses.AddRange(mylife, glooko);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylife), ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(expectedGroups);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_WhenTwoGroupsShareTheValue()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // Two same-source doses of 2.0U four minutes apart stay separate groups, and both sit
        // inside the incoming record's wide window.
        var first = CreateBolus(WideBase, 2.0, "mylife-connector");
        var second = CreateBolus(WideBase + 240_000, 2.0, "mylife-connector");
        var incoming = CreateBolus(WideBase + 120_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(first, second, incoming);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(first), ToInput(second)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(incoming)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(3);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(3,
            "two candidate groups mean the dose cannot be attributed to one of them, so it stays separate");
        var incomingCanonical = links.Single(lr => lr.RecordId == incoming.Id).CanonicalId;
        incomingCanonical.Should().NotBe(links.Single(lr => lr.RecordId == first.Id).CanonicalId);
        incomingCanonical.Should().NotBe(links.Single(lr => lr.RecordId == second.Id).CanonicalId);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_ForSameSourcePair()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // One connector reporting 2.0U twice, 64 seconds apart, is two real doses.
        var first = CreateBolus(WideBase, 2.0, "mylife-connector");
        var second = CreateBolus(WideBase + 64_000, 2.0, "mylife-connector");
        context.Boluses.AddRange(first, second);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(first)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(second)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_WhenInsulinDiffersWithinTheTightTolerance()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // 0.04U apart is inside the tight path's +/-0.05U tolerance; the wide path is exact.
        var mylife = CreateBolus(WideBase, 2.00, "mylife-connector");
        var glooko = CreateBolus(WideBase + 64_000, 2.04, "glooko-connector");
        context.Boluses.AddRange(mylife, glooko);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylife)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_ForSensorGlucose()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // Sensor glucose repeats the same value all day, so it is excluded from the wide window.
        var mylife = CreateSensorGlucose(WideBase, 120, "mylife-connector");
        var glooko = CreateSensorGlucose(WideBase + 64_000, 120, "glooko-connector");
        context.SensorGlucose.AddRange(mylife, glooko);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.SensorGlucose, [ToInput(mylife)]);
        await service.DeduplicateBatchAsync(RecordType.SensorGlucose, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Theory]
    [InlineData(DeduplicationInput.UnknownDataSource)]
    [InlineData("")]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_WhenTheIncomingDataSourceIsUnknown(string dataSource)
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // Every repository substitutes the unknown sentinel for a record with no source — a
        // manually entered dose, typically. It names no connector, so it cannot show that this is
        // a second connector's copy rather than a second real dose.
        var known = CreateBolus(WideBase, 2.0, "mylife-connector");
        var sourceless = CreateBolus(WideBase + 64_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(known, sourceless);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(known)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(sourceless, dataSource: dataSource)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_WhenTheCandidateGroupHasNoKnownSource()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // The sentinel is equally uninformative on the group's side of the comparison: it is not
        // "a different connector", so it cannot be treated as disjoint from a named one.
        var sourceless = CreateBolus(WideBase, 2.0, DeduplicationInput.UnknownDataSource);
        var glooko = CreateBolus(WideBase + 64_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(sourceless, glooko);
        AddPrimaryLink(context, RecordType.Bolus, sourceless.Id, WideBase, DeduplicationInput.UnknownDataSource);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_WhenTheOnlyCandidateIsSoftDeleted()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // A soft-deleted record is hidden from reads, so joining its group would hide the incoming
        // record behind it.
        var deleted = CreateBolus(WideBase, 2.0, "mylife-connector");
        deleted.DeletedAt = DateTime.UtcNow;
        var glooko = CreateBolus(WideBase + 64_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(deleted, glooko);
        AddPrimaryLink(context, RecordType.Bolus, deleted.Id, WideBase, "mylife-connector");
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glooko)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeduplicateBatchAsync_WideCandidateCount_IsIndependentOfBatching(bool singleBatch)
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // Two existing groups 20 minutes apart, and two incoming records between them. The first
        // incoming record can only see the earlier group and joins it; the second sees the later
        // group AND the group the first record just joined, which makes it ambiguous. That must
        // hold whether the two arrive together or in sequence — the ambiguity guard counts
        // candidates in the data, not in the batch.
        var glookoSeed = CreateBolus(WideBase, 2.0, "glooko-connector");
        var mylifeSeed = CreateBolus(WideBase + 1_200_000, 2.0, "mylife-connector");
        var mylifeIncoming = CreateBolus(WideBase + 540_000, 2.0, "mylife-connector");
        var glookoIncoming = CreateBolus(WideBase + 1_080_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(glookoSeed, mylifeSeed, mylifeIncoming, glookoIncoming);
        AddPrimaryLink(context, RecordType.Bolus, glookoSeed.Id, WideBase, "glooko-connector");
        AddPrimaryLink(context, RecordType.Bolus, mylifeSeed.Id, WideBase + 1_200_000, "mylife-connector");
        await context.SaveChangesAsync();

        if (singleBatch)
        {
            await service.DeduplicateBatchAsync(
                RecordType.Bolus, [ToInput(mylifeIncoming), ToInput(glookoIncoming)]);
        }
        else
        {
            await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylifeIncoming)]);
            await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glookoIncoming)]);
        }

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(4);

        var glookoSeedCanonical = links.Single(lr => lr.RecordId == glookoSeed.Id).CanonicalId;
        var mylifeSeedCanonical = links.Single(lr => lr.RecordId == mylifeSeed.Id).CanonicalId;
        links.Single(lr => lr.RecordId == mylifeIncoming.Id).CanonicalId.Should().Be(glookoSeedCanonical,
            "the first incoming record has exactly one candidate group");

        var ambiguous = links.Single(lr => lr.RecordId == glookoIncoming.Id).CanonicalId;
        ambiguous.Should().NotBe(glookoSeedCanonical);
        ambiguous.Should().NotBe(mylifeSeedCanonical);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_WideJoin_PromotesAnEarlierRecordToPrimary()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // The wide window reaches backwards as well as forwards, so a joining record can predate
        // the group's primary. The group's event time must not depend on which connector synced
        // first.
        var glooko = CreateBolus(WideBase + 120_000, 2.0, "glooko-connector");
        var mylife = CreateBolus(WideBase, 2.0, "mylife-connector");
        context.Boluses.AddRange(glooko, mylife);
        AddPrimaryLink(context, RecordType.Bolus, glooko.Id, WideBase + 120_000, "glooko-connector");
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1, "exactly one record in the group is primary");
        links.Single(lr => lr.IsPrimary).RecordId.Should().Be(mylife.Id,
            "the earlier record is the group's event time");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_WideJoin_LeavesPrimaryAloneWhenTheJoinerIsLater()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var glooko = CreateBolus(WideBase, 2.0, "glooko-connector");
        var mylife = CreateBolus(WideBase + 120_000, 2.0, "mylife-connector");
        context.Boluses.AddRange(glooko, mylife);
        AddPrimaryLink(context, RecordType.Bolus, glooko.Id, WideBase, "glooko-connector");
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1);
        links.Single(lr => lr.IsPrimary).RecordId.Should().Be(glooko.Id);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_WideJoin_KeepsALiveRecordPrimaryOverASoftDeletedEarlierOne()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // Reads hide soft-deleted records and non-primary links alike, so promoting the deleted
        // record would leave the group rendering nothing at all — a real dose invisible for good.
        var deleted = CreateBolus(WideBase, 2.0, "mylife-connector");
        deleted.DeletedAt = DateTime.UtcNow;
        var live = CreateBolus(WideBase + 60_000, 2.0, "glooko-connector");
        var joiner = CreateBolus(WideBase + 120_000, 2.0, "libre-connector");
        context.Boluses.AddRange(deleted, live, joiner);

        var canonicalId = AddPrimaryLink(context, RecordType.Bolus, live.Id, WideBase + 60_000, "glooko-connector");
        AddLink(context, RecordType.Bolus, deleted.Id, WideBase, "mylife-connector", canonicalId, isPrimary: false);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(joiner)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(3);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1);
        links.Single(lr => lr.IsPrimary).RecordId.Should().Be(live.Id,
            "the earliest record that reads can actually show stays primary");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_WideJoin_KeepsALiveRecordPrimaryOverAnOrphanedEarlierLink()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // An orphaned link points at a record that no longer exists, so promoting it would hide
        // the group for the same reason a deleted record would.
        var live = CreateBolus(WideBase + 60_000, 2.0, "glooko-connector");
        var joiner = CreateBolus(WideBase + 120_000, 2.0, "libre-connector");
        context.Boluses.AddRange(live, joiner);

        var canonicalId = AddPrimaryLink(context, RecordType.Bolus, live.Id, WideBase + 60_000, "glooko-connector");
        AddLink(context, RecordType.Bolus, Guid.CreateVersion7(), WideBase, "mylife-connector", canonicalId, isPrimary: false);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(joiner)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Count(lr => lr.IsPrimary).Should().Be(1);
        links.Single(lr => lr.IsPrimary).RecordId.Should().Be(live.Id);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_WideJoin_PromotesTheEarlierRecordOfAChunkMintedGroup()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // A batch of 500 or fewer records is not sorted, so a bulk upload carrying two sources can
        // mint the group on its later record and join the earlier one to it.
        var glooko = CreateBolus(WideBase + 1_080_000, 2.0, "glooko-connector");
        var mylife = CreateBolus(WideBase + 540_000, 2.0, "mylife-connector");
        context.Boluses.AddRange(glooko, mylife);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glooko), ToInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1);
        links.Single(lr => lr.IsPrimary).RecordId.Should().Be(mylife.Id,
            "the group's event time does not depend on the order records arrived in the batch");
    }

    [Fact]
    public async Task DeduplicateBatchAsync_MatchesCrossSourceTempBasal_WithEqualDurations()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var start = ToUtc(WideBase);
        var glooko = CreateTestTempBasalEntity(
            startTimestamp: start, rate: 1.2, origin: "Scheduled", dataSource: "glooko-connector",
            duration: TimeSpan.FromMinutes(30));
        var mylife = CreateTestTempBasalEntity(
            startTimestamp: start.AddSeconds(64), rate: 1.2, origin: "Scheduled", dataSource: "mylife-connector",
            duration: TimeSpan.FromMinutes(30));
        context.TempBasals.AddRange(glooko, mylife);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.TempBasal, [ToDeduplicationInput(glooko)]);
        await service.DeduplicateBatchAsync(RecordType.TempBasal, [ToDeduplicationInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
        links.Count(lr => lr.IsPrimary).Should().Be(1);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_ForOpenEndedTempBasals()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // A running temp basal has no end yet, so rate alone would be the whole comparison.
        var start = ToUtc(WideBase);
        var glooko = CreateTestTempBasalEntity(
            startTimestamp: start, rate: 1.2, origin: "Scheduled", dataSource: "glooko-connector");
        var mylife = CreateTestTempBasalEntity(
            startTimestamp: start.AddSeconds(64), rate: 1.2, origin: "Scheduled", dataSource: "mylife-connector");
        context.TempBasals.AddRange(glooko, mylife);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.TempBasal, [ToDeduplicationInput(glooko)]);
        await service.DeduplicateBatchAsync(RecordType.TempBasal, [ToDeduplicationInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_WhenTempBasalDurationsDiffer()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var start = ToUtc(WideBase);
        var glooko = CreateTestTempBasalEntity(
            startTimestamp: start, rate: 1.2, origin: "Scheduled", dataSource: "glooko-connector",
            duration: TimeSpan.FromMinutes(30));
        var mylife = CreateTestTempBasalEntity(
            startTimestamp: start.AddSeconds(64), rate: 1.2, origin: "Scheduled", dataSource: "mylife-connector",
            duration: TimeSpan.FromMinutes(45));
        context.TempBasals.AddRange(glooko, mylife);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.TempBasal, [ToDeduplicationInput(glooko)]);
        await service.DeduplicateBatchAsync(RecordType.TempBasal, [ToDeduplicationInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_RefusesWideMatch_ForCorrectionOnlyBolusCalculations()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // A correction-only calculation carries no carb input, which the criteria report as 0.
        // Every such calculation would otherwise look exactly equal to every other.
        var glooko = CreateBolusCalculation(WideBase, carbInput: null, "glooko-connector");
        var mylife = CreateBolusCalculation(WideBase + 64_000, carbInput: null, "mylife-connector");
        context.BolusCalculations.AddRange(glooko, mylife);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.BolusCalculation, [ToInput(glooko)]);
        await service.DeduplicateBatchAsync(RecordType.BolusCalculation, [ToInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_MatchesCrossSourceBolusCalculation_WhenCarbsArePresent()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        var glooko = CreateBolusCalculation(WideBase, carbInput: 45, "glooko-connector");
        var mylife = CreateBolusCalculation(WideBase + 64_000, carbInput: 45, "mylife-connector");
        context.BolusCalculations.AddRange(glooko, mylife);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.BolusCalculation, [ToInput(glooko)]);
        await service.DeduplicateBatchAsync(RecordType.BolusCalculation, [ToInput(mylife)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CriteriaMatch_ExactMode_RejectsTypesOutsideTheWideWindow(bool exact, bool expected)
    {
        // Every production caller checks WideMatchableTypes before reaching exact mode, so this
        // guard is unreachable through the batch and reconcile paths and can only be pinned here.
        var a = new MatchCriteria { GlucoseValue = 120, GlucoseTolerance = 1.0 };
        var b = new MatchCriteria { GlucoseValue = 120, GlucoseTolerance = 1.0 };

        DeduplicationService.CriteriaMatch(RecordType.SensorGlucose, a, b, exact).Should().Be(expected);
    }

    [Fact]
    public async Task DeduplicateBatchAsync_Note_DoesNotMatchASoftDeletedNote()
    {
        // Notes match on the time window alone, so nothing about the note's own content can keep a
        // deleted one out of range — only the deleted check can.
        await using var context = NewContext();
        var service = CreateService(context);

        var timestamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var deleted = new NoteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Text = "removed",
            DataSource = "mylife-connector",
            DeletedAt = DateTime.UtcNow
        };
        var fresh = new NoteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = timestamp.AddSeconds(10),
            Text = "kept",
            DataSource = "glooko-connector"
        };
        context.Notes.AddRange(deleted, fresh);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Note, [ToInput(deleted)]);
        await service.DeduplicateBatchAsync(RecordType.Note, [ToInput(fresh)]);

        var links = await context.LinkedRecords.IgnoreQueryFilters()
            .Where(lr => lr.RecordType == "note").ToListAsync();
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(
            2, "a soft-deleted note is not a candidate for a live one to join");
    }

    [Fact]
    public void CriteriaMatch_DeviceEvent_RefusesWhenTheEventTypeIsAbsent()
    {
        // The event type is the whole value, so two events that carry none compare equal on nothing.
        var blank = new MatchCriteria { EventType = "" };

        DeduplicationService.CriteriaMatch(RecordType.DeviceEvent, blank, blank).Should().BeFalse();
        DeduplicationService.CriteriaMatch(
            RecordType.DeviceEvent,
            new MatchCriteria { EventType = "Site Change" },
            new MatchCriteria { EventType = "site change" }).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Automatic", false)]
    [InlineData("Automatic", "", false)]
    [InlineData("", "", true)]
    [InlineData("Automatic", "automatic", true)]
    public void CriteriaMatch_StateSpan_RequiresTheStatesToAgree(string storedState, string incomingState, bool expected)
    {
        // A span posted without a state is stored as "", so treating an absent state as a wildcard
        // would let one join every other span of its category — and would answer differently
        // depending on which side it was passed as.
        var stored = new MatchCriteria { Category = StateSpanCategory.PumpMode, State = storedState };
        var incoming = new MatchCriteria { Category = StateSpanCategory.PumpMode, State = incomingState };

        DeduplicationService.CriteriaMatch(RecordType.StateSpan, stored, incoming).Should().Be(expected);
        DeduplicationService.CriteriaMatch(RecordType.StateSpan, incoming, stored).Should().Be(
            expected, "the merge pass compares stored spans in both orders");
    }

    [Theory]
    [InlineData("Exercise", true)]
    [InlineData("exercise", true)]
    [InlineData("NotACategory", false)]
    [InlineData("3", false)]
    [InlineData("", false)]
    public void MatchCriteriaMapper_StateSpan_ReadsOnlyADeclaredCategoryName(string stored, bool parsed)
    {
        // Enum.TryParse accepts the numeric form, so "3" would otherwise become a real category.
        var criteria = MatchCriteriaMapper.From(new StateSpanEntity { Category = stored, State = "active" });

        criteria.Category.HasValue.Should().Be(parsed);
    }

    [Fact]
    public void CriteriaMatch_StateSpan_RefusesWhenTheCategoryIsUnparseable()
    {
        // A category the mapper could not read becomes null; without a presence check every such
        // span in the window would compare equal to every other and collapse into one group.
        var unreadable = new MatchCriteria { Category = null, State = "active" };

        DeduplicationService.CriteriaMatch(RecordType.StateSpan, unreadable, unreadable).Should().BeFalse();
        DeduplicationService.CriteriaMatch(
            RecordType.StateSpan,
            new MatchCriteria { Category = StateSpanCategory.Exercise, State = "active" },
            new MatchCriteria { Category = StateSpanCategory.Exercise, State = "active" }).Should().BeTrue();
    }

    [Fact]
    public async Task DeduplicateBatchAsync_WideMatch_AdmitsOnlyTheFirstSameSourceRecordOfABatch()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // An existing glooko group at 12:02 and a mylife batch holding 12:00 and 12:04. The first
        // mylife record joins; the second must see mylife already in the group and refuse.
        var glooko = CreateBolus(WideBase + 120_000, 2.0, "glooko-connector");
        var mylifeEarly = CreateBolus(WideBase, 2.0, "mylife-connector");
        var mylifeLate = CreateBolus(WideBase + 240_000, 2.0, "mylife-connector");
        context.Boluses.AddRange(glooko, mylifeEarly, mylifeLate);
        await context.SaveChangesAsync();

        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(glooko)]);
        await service.DeduplicateBatchAsync(RecordType.Bolus, [ToInput(mylifeEarly), ToInput(mylifeLate)]);

        var links = await context.LinkedRecords.ToListAsync();
        links.Should().HaveCount(3);
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);

        var glookoCanonical = links.Single(lr => lr.RecordId == glooko.Id).CanonicalId;
        links.Single(lr => lr.RecordId == mylifeEarly.Id).CanonicalId.Should().Be(glookoCanonical,
            "the first mylife record joins the glooko group");
        links.Single(lr => lr.RecordId == mylifeLate.Id).CanonicalId.Should().NotBe(glookoCanonical,
            "the group already holds a mylife record, so the second one stays separate");
    }

    [Fact]
    public async Task DeduplicateAllAsync_HealsPreExistingCrossSourceGroups()
    {
        await using var context = NewContext();
        var service = CreateService(context);

        // History ingested while the connectors' clocks were drifting: the same dose landed in two
        // canonical groups. Both records are already linked, so a re-run creates no links at all
        // and only the job's merge pass can collapse them.
        var mylife = CreateBolus(WideBase, 2.0, "mylife-connector");
        var glooko = CreateBolus(WideBase + 64_000, 2.0, "glooko-connector");
        context.Boluses.AddRange(mylife, glooko);
        AddPrimaryLink(context, RecordType.Bolus, mylife.Id, WideBase, "mylife-connector");
        AddPrimaryLink(context, RecordType.Bolus, glooko.Id, WideBase + 64_000, "glooko-connector");
        await context.SaveChangesAsync();

        var result = await service.DeduplicateAllAsync();

        result.Success.Should().BeTrue();
        var links = await context.LinkedRecords.Where(lr => lr.RecordType == "bolus").ToListAsync();
        links.Should().HaveCount(2, "the full run links nothing new");
        links.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "a full re-dedup run heals groups split by clock drift");
        links.Count(lr => lr.IsPrimary).Should().Be(1);
    }

    #endregion

    #region Test Helper Methods

    /// <summary>
    /// Event time the wide-window tests are anchored on, in Unix milliseconds.
    /// </summary>
    private static readonly long WideBase =
        new DateTimeOffset(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

    private NocturneDbContext NewContext()
    {
        var context = new NocturneDbContext(_contextOptions) { TenantId = TestTenantId };
        return context;
    }

    private DeduplicationService CreateService(NocturneDbContext context) =>
        new(context,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<DeduplicationService>>().Object);

    private static DateTime ToUtc(long mills) =>
        DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;

    private static long ToMills(DateTime timestamp) =>
        new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static BolusEntity CreateBolus(long mills, double insulin, string dataSource) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = ToUtc(mills),
            Insulin = insulin,
            DataSource = dataSource
        };

    private static CarbIntakeEntity CreateCarbIntake(long mills, double carbs, string dataSource) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = ToUtc(mills),
            Carbs = carbs,
            DataSource = dataSource
        };

    private static DeviceEventEntity CreateDeviceEvent(long mills, string eventType, string dataSource) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = ToUtc(mills),
            EventType = eventType,
            DataSource = dataSource
        };

    /// <summary>
    /// Seeds a primary link in its own canonical group, standing in for history linked by an
    /// earlier ingest run, and returns that canonical id.
    /// </summary>
    private static Guid AddPrimaryLink(
        NocturneDbContext context, RecordType recordType, Guid recordId, long mills, string dataSource)
    {
        var canonicalId = Guid.CreateVersion7();
        AddLink(context, recordType, recordId, mills, dataSource, canonicalId, isPrimary: true);
        return canonicalId;
    }

    /// <summary>
    /// Seeds a link into an existing canonical group, standing in for a group an earlier run
    /// already collapsed.
    /// </summary>
    private static void AddLink(
        NocturneDbContext context,
        RecordType recordType,
        Guid recordId,
        long mills,
        string dataSource,
        Guid canonicalId,
        bool isPrimary) =>
        context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            CanonicalId = canonicalId,
            RecordType = recordType.ToString().ToLowerInvariant(),
            RecordId = recordId,
            SourceTimestamp = mills,
            DataSource = dataSource,
            IsPrimary = isPrimary,
            SysCreatedAt = DateTime.UtcNow
        });

    private static BolusCalculationEntity CreateBolusCalculation(long mills, double? carbInput, string dataSource) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = ToUtc(mills),
            CarbInput = carbInput,
            DataSource = dataSource
        };

    private static SensorGlucoseEntity CreateSensorGlucose(long mills, double mgdl, string dataSource) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = ToUtc(mills),
            Mgdl = mgdl,
            DataSource = dataSource
        };

    // Criteria come from MatchCriteriaMapper, the same source the repositories feed the service
    // from, so a change to a tolerance cannot pass here while breaking production.
    private static DeduplicationInput ToInput(BolusEntity e, string? dataSource = null) =>
        new(e.Id, ToMills(e.Timestamp), dataSource ?? e.DataSource ?? DeduplicationInput.UnknownDataSource,
            MatchCriteriaMapper.From(e));

    private static DeduplicationInput ToInput(CarbIntakeEntity e, string? dataSource = null) =>
        new(e.Id, ToMills(e.Timestamp), dataSource ?? e.DataSource ?? DeduplicationInput.UnknownDataSource,
            MatchCriteriaMapper.From(e));

    private static DeduplicationInput ToInput(DeviceEventEntity e, string? dataSource = null) =>
        new(e.Id, ToMills(e.Timestamp), dataSource ?? e.DataSource ?? DeduplicationInput.UnknownDataSource,
            MatchCriteriaMapper.From(e));

    private static DeduplicationInput ToInput(SensorGlucoseEntity e, string? dataSource = null) =>
        new(e.Id, ToMills(e.Timestamp), dataSource ?? e.DataSource ?? DeduplicationInput.UnknownDataSource,
            MatchCriteriaMapper.From(e));

    private static DeduplicationInput ToInput(BolusCalculationEntity e, string? dataSource = null) =>
        new(e.Id, ToMills(e.Timestamp), dataSource ?? e.DataSource ?? DeduplicationInput.UnknownDataSource,
            MatchCriteriaMapper.From(e));

    private static DeduplicationInput ToInput(NoteEntity e, string? dataSource = null) =>
        new(e.Id, ToMills(e.Timestamp), dataSource ?? e.DataSource ?? DeduplicationInput.UnknownDataSource,
            MatchCriteriaMapper.ForNote());

    private static StateSpan CreateTestStateSpan(
        StateSpanCategory category,
        string state,
        long startMills,
        string source,
        long? endMills = null
    )
    {
        return new StateSpan
        {
            Id = Guid.NewGuid().ToString(),
            Category = category,
            State = state,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
            EndTimestamp = endMills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(endMills.Value).UtcDateTime : null,
            Source = source,
            OriginalId = $"{source}_{startMills}",
            Metadata = new Dictionary<string, object>
            {
                { "rate", 1.0 },
                { "origin", "Manual" }
            }
        };
    }

    private static DeduplicationInput ToDeduplicationInput(TempBasalEntity entity) =>
        new(
            RecordId: entity.Id,
            Mills: new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DataSource: entity.DataSource ?? DeduplicationInput.UnknownDataSource,
            Criteria: MatchCriteriaMapper.From(entity));

    private static TempBasalEntity CreateTestTempBasalEntity(
        DateTime startTimestamp,
        double rate,
        string origin,
        string dataSource,
        string? legacyId = null,
        TimeSpan? duration = null
    )
    {
        return new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            StartTimestamp = startTimestamp,
            EndTimestamp = duration.HasValue ? startTimestamp + duration.Value : null,
            Rate = rate,
            Origin = origin,
            DataSource = dataSource,
            LegacyId = legacyId ?? $"{dataSource}_{startTimestamp.Ticks}_{Guid.NewGuid():N}",
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        };
    }

    #endregion

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _connection?.Dispose();
    }
}
