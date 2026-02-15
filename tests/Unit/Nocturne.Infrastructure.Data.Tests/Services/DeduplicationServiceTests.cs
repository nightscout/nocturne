using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts;
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

        // Create the database schema
        using var context = new NocturneDbContext(_contextOptions);
        context.Database.EnsureCreated();

        // Set up DI container for IServiceScopeFactory
        var services = new ServiceCollection();
        services.AddDbContext<NocturneDbContext>(options =>
            options.UseSqlite(_connection));
        services.AddScoped<IDeduplicationService, DeduplicationService>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();
    }

    #region Basal Type Deduplication Tests

    [Fact]
    public async Task DeduplicateAllAsync_ShouldGroupBasalAndTempBasal_WhenAtSameTimestamp()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Use a timestamp well within a 30-second window to avoid boundary issues
        // Window key = mills / 30000, so pick a timestamp in the middle of a window
        var baseTime = 30000L * 1000; // Start of window 1000
        var timestamp = baseTime + 10000; // 10 seconds into the window

        // Create a Basal and Temp Basal at the same timestamp with SAME rate
        var basalTreatment = CreateTestTreatment(
            eventType: "Basal",
            mills: timestamp,
            rate: 0.8
        );
        var tempBasalTreatment = CreateTestTreatment(
            eventType: "Temp Basal",
            mills: timestamp + 1000, // 1 second later, still in same window
            rate: 0.8, // SAME rate - they should be grouped
            duration: 60
        );

        var basalEntity = TreatmentMapper.ToEntity(basalTreatment);
        var tempBasalEntity = TreatmentMapper.ToEntity(tempBasalTreatment);

        // Verify Rate is being set correctly
        basalEntity.Basal.Rate.Should().Be(0.8);
        tempBasalEntity.Basal.Rate.Should().Be(0.8);

        context.Treatments.AddRange(basalEntity, tempBasalEntity);
        await context.SaveChangesAsync();

        // Verify data in database
        var savedTreatments = await context.Treatments.ToListAsync();
        savedTreatments.Should().HaveCount(2);
        savedTreatments.All(t => t.Basal.Rate == 0.8).Should().BeTrue("All treatments should have rate 0.8");

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TreatmentsProcessed.Should().Be(2);

        // Verify linked records
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2, "Both treatments should be linked");

        var canonicalIds = linkedRecords.Select(lr => lr.CanonicalId).Distinct().ToList();
        canonicalIds.Should().HaveCount(1, "Both treatments should share the same canonical ID since they have the same rate and are in the same time window");

        // Should be 1 duplicate group (since 2 items are grouped)
        result.DuplicateGroupsFound.Should().Be(1);

        // Verify Temp Basal is the primary record (higher priority)
        var primaryRecord = linkedRecords.First(lr => lr.IsPrimary);
        var primaryTreatment = await context.Treatments.FindAsync(primaryRecord.RecordId);
        primaryTreatment!.EventType.Should().Be("Temp Basal");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldPreferTempBasal_WhenMergingWithBasal()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Use a timestamp well within a 30-second window
        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        // Create Basal first (earlier timestamp), then Temp Basal
        var basalTreatment = CreateTestTreatment(
            eventType: "Basal",
            mills: timestamp,
            rate: 1.0
        );
        var tempBasalTreatment = CreateTestTreatment(
            eventType: "Temp Basal",
            mills: timestamp + 5000, // 5 seconds later, within window
            rate: 1.0,
            duration: 30
        );

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(basalTreatment),
            TreatmentMapper.ToEntity(tempBasalTreatment)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();

        // Get the canonical ID from linked records
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        var canonicalId = linkedRecords.First().CanonicalId;

        // Get the unified treatment
        var unified = await service.GetUnifiedTreatmentAsync(canonicalId);

        // The unified treatment should have EventType = "Temp Basal"
        unified.Should().NotBeNull();
        unified!.EventType.Should().Be("Temp Basal");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldNotGroupDifferentEventTypes_WhenNotBasalRelated()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Use a timestamp well within a 30-second window
        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        // Create a Meal Bolus and Correction Bolus at the same timestamp
        var mealBolus = CreateTestTreatment(
            eventType: "Meal Bolus",
            mills: timestamp,
            insulin: 5.0
        );
        var correctionBolus = CreateTestTreatment(
            eventType: "Correction Bolus",
            mills: timestamp + 1000,
            insulin: 2.0
        );

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(mealBolus),
            TreatmentMapper.ToEntity(correctionBolus)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();

        // Should create 2 separate groups (different event types)
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldGroupMultipleBasals_WithinTimeWindow()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Use a timestamp well within a 30-second window
        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 5000; // 5 seconds into window, leaving room for +10s

        // Create multiple basal-related treatments within the same time window
        var basal1 = CreateTestTreatment(eventType: "Basal", mills: timestamp, rate: 0.8);
        var basal2 = CreateTestTreatment(eventType: "Basal", mills: timestamp + 5000, rate: 0.8);
        var tempBasal = CreateTestTreatment(eventType: "Temp Basal", mills: timestamp + 10000, rate: 0.8, duration: 60);

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(basal1),
            TreatmentMapper.ToEntity(basal2),
            TreatmentMapper.ToEntity(tempBasal)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TreatmentsProcessed.Should().Be(3);

        // All three should be grouped together
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(3);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1);

        // Temp Basal should be primary
        var primaryRecord = linkedRecords.First(lr => lr.IsPrimary);
        var primaryTreatment = await context.Treatments.FindAsync(primaryRecord.RecordId);
        primaryTreatment!.EventType.Should().Be("Temp Basal");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldNotGroupBasals_OutsideTimeWindow()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Use timestamps in different 30-second windows
        var window1 = 30000L * 1000; // Window 1000
        var window2 = 30000L * 1002; // Window 1002 (60 seconds later)

        // Create basals in different windows
        var basal1 = CreateTestTreatment(eventType: "Basal", mills: window1 + 10000, rate: 0.8);
        var basal2 = CreateTestTreatment(eventType: "Temp Basal", mills: window2 + 10000, rate: 1.2);

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(basal1),
            TreatmentMapper.ToEntity(basal2)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();

        // Should create 2 separate groups (outside time window)
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task GetUnifiedTreatmentAsync_ShouldReturnTempBasalEventType_WhenMixedBasalTypes()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        // Use a timestamp well within a 30-second window
        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        // Create Basal with notes, Temp Basal with duration
        // Use different DataSources to verify sources are properly merged
        var basalTreatment = CreateTestTreatment(
            eventType: "Basal",
            mills: timestamp,
            rate: 0.8
        );
        basalTreatment.Notes = "From pump";
        basalTreatment.DataSource = "pump-source";

        var tempBasalTreatment = CreateTestTreatment(
            eventType: "Temp Basal",
            mills: timestamp + 2000,
            rate: 0.8,
            duration: 30
        );
        tempBasalTreatment.Percent = 100;
        tempBasalTreatment.DataSource = "mylife-source";

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(basalTreatment),
            TreatmentMapper.ToEntity(tempBasalTreatment)
        );
        await context.SaveChangesAsync();

        // Act - First deduplicate
        await service.DeduplicateAllAsync();

        // Verify both treatments are linked to the same canonical ID
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2, "Both treatments should be linked");
        var distinctCanonicalIds = linkedRecords.Select(lr => lr.CanonicalId).Distinct().ToList();
        distinctCanonicalIds.Should().HaveCount(1, "Both treatments should share the same canonical ID");

        // Get the unified treatment
        var unified = await service.GetUnifiedTreatmentAsync(distinctCanonicalIds[0]);

        // Assert
        unified.Should().NotBeNull();
        unified!.EventType.Should().Be("Temp Basal", "Temp Basal should be preferred over Basal");
        unified.Sources.Should().HaveCount(2, "Both data sources should be included");
        unified.Sources.Should().Contain("pump-source");
        unified.Sources.Should().Contain("mylife-source");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldHandleSingleBasal_WithoutError()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        var basalTreatment = CreateTestTreatment(eventType: "Basal", mills: timestamp, rate: 0.8);
        context.Treatments.Add(TreatmentMapper.ToEntity(basalTreatment));
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TreatmentsProcessed.Should().Be(1);
        result.DuplicateGroupsFound.Should().Be(0); // Single item, not a duplicate group
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldHandleSingleTempBasal_WithoutError()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        var tempBasalTreatment = CreateTestTreatment(eventType: "Temp Basal", mills: timestamp, rate: 1.2, duration: 60);
        context.Treatments.Add(TreatmentMapper.ToEntity(tempBasalTreatment));
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TreatmentsProcessed.Should().Be(1);

        var linkedRecord = await context.LinkedRecords.FirstAsync();
        var unified = await service.GetUnifiedTreatmentAsync(linkedRecord.CanonicalId);
        unified!.EventType.Should().Be("Temp Basal");
    }

    [Fact]
    public async Task DeduplicateAllAsync_ShouldPreserveTempBasalProperties_WhenMerging()
    {
        // Arrange
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        // Create treatments with same rate so they get grouped together
        var basalTreatment = CreateTestTreatment(eventType: "Basal", mills: timestamp, rate: 1.2);
        var tempBasalTreatment = CreateTestTreatment(
            eventType: "Temp Basal",
            mills: timestamp + 1000,
            rate: 1.2,
            duration: 45
        );
        tempBasalTreatment.Percent = 150;

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(basalTreatment),
            TreatmentMapper.ToEntity(tempBasalTreatment)
        );
        await context.SaveChangesAsync();

        // Act
        await service.DeduplicateAllAsync();

        // Verify both are in the same canonical group
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        var canonicalId = linkedRecords.First().CanonicalId;
        linkedRecords.All(lr => lr.CanonicalId == canonicalId).Should().BeTrue();

        // Get the unified treatment
        var unified = await service.GetUnifiedTreatmentAsync(canonicalId);

        // Assert - The unified treatment should have EventType = Temp Basal
        // because GetPreferredEventType returns the highest priority basal type
        unified.Should().NotBeNull();
        unified!.EventType.Should().Be("Temp Basal", "Temp Basal should be preferred over Basal");

        // Note: The primary treatment is the one with highest priority (Temp Basal)
        // because we sort by priority before selecting primary in DeduplicateTreatmentsAsync
        // The primary's values should be used for Duration, Percent, Rate
        unified.Duration.Should().Be(45);
        unified.Percent.Should().Be(150);
        unified.Rate.Should().Be(1.2);
    }

    #endregion

    [Fact]
    public async Task Debug_ValueGrouping_ShouldGroupBasalsWithSameRate()
    {
        // This test directly verifies the grouping logic
        await using var context = new NocturneDbContext(_contextOptions);
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = new Mock<ILogger<DeduplicationService>>();
        var service = new DeduplicationService(context, scopeFactory, logger.Object);

        var baseTime = 30000L * 1000;
        var timestamp = baseTime + 10000;

        // Note: For Temp Basal with Rate and Duration, the Treatment.Insulin getter
        // calculates Insulin = Rate * (Duration/60). This is intentional behavior.
        // The deduplication should group by Rate for basal types, not by calculated Insulin.
        var basal = new Treatment
        {
            Id = Guid.NewGuid().ToString(),
            Mills = timestamp,
            Created_at = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            EventType = "Basal",
            Rate = 0.8,
            EnteredBy = "test",
            DataSource = "test-source"
        };
        var tempBasal = new Treatment
        {
            Id = Guid.NewGuid().ToString(),
            Mills = timestamp + 1000,
            Created_at = DateTimeOffset.FromUnixTimeMilliseconds(timestamp + 1000).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            EventType = "Temp Basal",
            Rate = 0.8,
            Duration = 60,
            EnteredBy = "test",
            DataSource = "test-source"
        };

        // Verify both have same Rate
        basal.Rate.Should().Be(0.8);
        tempBasal.Rate.Should().Be(0.8);

        // Note: tempBasal.Insulin = 0.8 * (60/60) = 0.8 (calculated getter)
        // This is intentional - but for deduplication, we should group by Rate, not Insulin

        context.Treatments.AddRange(
            TreatmentMapper.ToEntity(basal),
            TreatmentMapper.ToEntity(tempBasal)
        );
        await context.SaveChangesAsync();

        // Act
        var result = await service.DeduplicateAllAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.TreatmentsProcessed.Should().Be(2);

        // Both should be grouped together (same rate, same time window)
        var linkedRecords = await context.LinkedRecords
            .Where(lr => lr.RecordType == "treatment")
            .ToListAsync();
        linkedRecords.Should().HaveCount(2);
        linkedRecords.Select(lr => lr.CanonicalId).Distinct().Should().HaveCount(1,
            "Both treatments should share the same canonical ID since they have the same rate and are in the same time window");
    }

    #region Test Helper Methods

    private static Treatment CreateTestTreatment(
        string eventType = "Basal",
        long? mills = null,
        double? rate = null,
        double? duration = null,
        double? insulin = null,
        string? id = null
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
            Rate = rate,
            Duration = duration,
            Insulin = insulin,
            EnteredBy = "test",
            DataSource = "test-source"
        };
    }

    #endregion

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _connection?.Dispose();
    }
}
