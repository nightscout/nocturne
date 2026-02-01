using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Optimized test fixture that provides PostgreSQL container for integration tests
/// Implements efficient resource sharing and cleanup strategies for better performance
/// </summary>
public class TestDatabaseFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private static SharedContainerState? _sharedContainers;
    private static int _instanceCount = 0;

    // PostgreSQL properties
    public string PostgreSqlConnectionString { get; private set; } = string.Empty;
    public NocturneDbContext DbContext { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _initializationSemaphore.WaitAsync();
        try
        {
            // Use shared containers across all tests in the collection
            if (_sharedContainers == null)
            {
                using var measurement = TestPerformanceTracker.MeasureTest(
                    "SharedContainers.Initialize"
                );
                _sharedContainers = new SharedContainerState();
                await _sharedContainers.InitializeAsync();
            }

            Interlocked.Increment(ref _instanceCount);

            // Set connection string
            PostgreSqlConnectionString = _sharedContainers.PostgreSqlConnectionString;

            // Create PostgreSQL DbContext
            var options = new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(PostgreSqlConnectionString)
                .Options;

            DbContext = new NocturneDbContext(options);

            // Ensure database is created and migrated
            await DbContext.Database.EnsureCreatedAsync();
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _initializationSemaphore.WaitAsync();
        try
        {
            // Clean up test-specific database completely
            if (DbContext != null)
            {
                await DbContext.Database.EnsureDeletedAsync();
                await DbContext.DisposeAsync();
            }

            var remainingInstances = Interlocked.Decrement(ref _instanceCount);

            // Only dispose shared containers when no more instances are using them
            if (remainingInstances == 0 && _sharedContainers != null)
            {
                await _sharedContainers.DisposeAsync();
                _sharedContainers = null;
            }
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    /// <summary>
    /// Efficient cleanup that doesn't require expensive delete operations
    /// For PostgreSQL: Clear all tables
    /// </summary>
    public async Task CleanupAsync()
    {
        using var measurement = TestPerformanceTracker.MeasureTest("Database.Cleanup");

        // PostgreSQL cleanup - clear all data
        if (DbContext != null)
        {
            // Clear all entities in the correct order to avoid foreign key constraints
            DbContext.InAppNotifications.RemoveRange(DbContext.InAppNotifications);
            DbContext.Profiles.RemoveRange(DbContext.Profiles);
            DbContext.Settings.RemoveRange(DbContext.Settings);
            DbContext.Foods.RemoveRange(DbContext.Foods);
            DbContext.DeviceStatuses.RemoveRange(DbContext.DeviceStatuses);
            DbContext.Treatments.RemoveRange(DbContext.Treatments);
            DbContext.Entries.RemoveRange(DbContext.Entries);

            await DbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Shared container state to avoid recreating containers for each test
    /// </summary>
    private class SharedContainerState : IAsyncDisposable
    {
        private PostgreSqlContainer? _postgresContainer;

        public string PostgreSqlConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            // Start PostgreSQL container
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("nocturne_test")
                .WithUsername("test")
                .WithPassword("test")
                .WithPortBinding(5432, true)
                .Build();

            await _postgresContainer.StartAsync();
            PostgreSqlConnectionString = _postgresContainer.GetConnectionString();
        }

        public async ValueTask DisposeAsync()
        {
            if (_postgresContainer != null)
            {
                await _postgresContainer.StopAsync();
                await _postgresContainer.DisposeAsync();
            }
        }
    }
}
