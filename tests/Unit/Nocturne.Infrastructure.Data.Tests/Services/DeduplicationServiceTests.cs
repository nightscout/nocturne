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
            startTimestamp: timestamp.AddMinutes(2), // 2 minutes later, well outside 30s window
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
            DataSource = tb.DataSource,
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

    #region Test Helper Methods

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

    private static DeduplicationInput ToDeduplicationInput(TempBasalEntity entity)
    {
        return new DeduplicationInput(
            RecordId: entity.Id,
            Mills: new DateTimeOffset(entity.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DataSource: entity.DataSource ?? "unknown",
            Criteria: new MatchCriteria { Rate = entity.Rate, RateTolerance = 0.05 });
    }

    private static TempBasalEntity CreateTestTempBasalEntity(
        DateTime startTimestamp,
        double rate,
        string origin,
        string dataSource,
        string? legacyId = null
    )
    {
        return new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = startTimestamp,
            Rate = rate,
            Origin = origin,
            DataSource = dataSource,
            LegacyId = legacyId ?? $"{dataSource}_{startTimestamp.Ticks}",
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
