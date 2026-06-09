using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class DeduplicationReconciliationBackgroundServiceTests
{
    /// <summary>
    /// Sets up an in-memory SQLite NocturneDbContext seeded with two active tenants
    /// and one inactive tenant, returning the connection string.
    /// </summary>
    private static (IDisposable cleanup, string connectionString) CreateSqliteDb()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"DedupReconcileBgTest_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        var cleanup = new TempFileCleanup(dbPath);

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var context = new NocturneDbContext(options);
        context.Database.ExecuteSqlRaw(@"
            CREATE TABLE tenants (
                Id TEXT PRIMARY KEY,
                slug TEXT NOT NULL,
                display_name TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                last_reading_at TEXT,
                allow_access_requests INTEGER NOT NULL DEFAULT 1,
                onboarding_completed_at TEXT,
                sys_created_at TEXT NOT NULL,
                sys_updated_at TEXT NOT NULL
            )");

        void Insert(string slug, bool active) =>
            context.Database.ExecuteSqlRaw(
                "INSERT INTO tenants (Id, slug, display_name, is_active, allow_access_requests, sys_created_at, sys_updated_at) VALUES ({0}, {1}, {2}, {3}, 1, {4}, {5})",
                Guid.NewGuid().ToString(), slug, slug, active ? 1 : 0,
                DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O"));

        Insert("active-one", true);
        Insert("active-two", true);
        Insert("inactive-one", false);

        return (cleanup, connectionString);
    }

    private static IServiceProvider BuildServiceProvider(
        string connectionString,
        Mock<IDeduplicationService> dedupMock)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(
            new SqliteDbContextFactory(connectionString));

        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            return factory.CreateDbContext();
        });

        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = new Mock<ITenantAccessor>();
            mock.Setup(t => t.SetTenant(It.IsAny<TenantContext>()));
            return mock.Object;
        });

        services.AddScoped(_ => dedupMock.Object);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReconcileAllTenantsAsync_CallsReconcileOncePerActiveTenant()
    {
        // Arrange
        var (cleanup, connStr) = CreateSqliteDb();
        using var _ = cleanup;

        var dedupMock = new Mock<IDeduplicationService>();
        dedupMock
            .Setup(d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReconcileResult(0, true));

        var serviceProvider = BuildServiceProvider(connStr, dedupMock);

        var sut = new DeduplicationReconciliationBackgroundService(
            serviceProvider,
            NullLogger<DeduplicationReconciliationBackgroundService>.Instance);

        // Act
        await sut.ReconcileAllTenantsAsync(CancellationToken.None);

        // Assert — once per ACTIVE tenant (2), not the inactive one
        dedupMock.Verify(
            d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ReconcileAllTenantsAsync_OneTenantThrows_OtherTenantsStillReconciled()
    {
        // Arrange
        var (cleanup, connStr) = CreateSqliteDb();
        using var _ = cleanup;

        var callCount = 0;
        var dedupMock = new Mock<IDeduplicationService>();
        dedupMock
            .Setup(d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                // First active tenant throws; the loop must continue to the second.
                if (callCount == 1)
                    throw new InvalidOperationException("boom");
                return Task.FromResult(new ReconcileResult(0, true));
            });

        var serviceProvider = BuildServiceProvider(connStr, dedupMock);

        var sut = new DeduplicationReconciliationBackgroundService(
            serviceProvider,
            NullLogger<DeduplicationReconciliationBackgroundService>.Instance);

        // Act — must not throw despite the first tenant failing
        await sut.ReconcileAllTenantsAsync(CancellationToken.None);

        // Assert — both active tenants were attempted
        dedupMock.Verify(
            d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /// <summary>
    /// Simple IDbContextFactory that creates NocturneDbContext instances against a SQLite database file.
    /// </summary>
    private sealed class SqliteDbContextFactory(string connectionString) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite(connectionString)
                .Options;
            return new NocturneDbContext(options);
        }
    }

    /// <summary>
    /// Deletes a temporary SQLite database file on dispose.
    /// </summary>
    private sealed class TempFileCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
