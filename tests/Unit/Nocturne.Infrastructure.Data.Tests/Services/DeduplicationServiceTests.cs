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
    public async Task DeduplicateAllAsync_ShouldNotGroupTempBasals_WithDifferentOrigins()
    {
        // Arrange
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
            origin: "Algorithm", // Different origin
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
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2,
            "temp basals with different origins should not be grouped");
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
