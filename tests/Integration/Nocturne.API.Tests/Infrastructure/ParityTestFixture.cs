using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Shared fixture for parity tests that manages both:
/// - Nightscout 15.0.3 (via NightscoutContainer with MongoDB)
/// - Nocturne (via WebApplicationFactory with PostgreSQL)
///
/// Follows the same shared container pattern as TestDatabaseFixture.
/// </summary>
public class ParityTestFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private static SharedParityState? _sharedState;
    private static int _instanceCount;

    /// <summary>
    /// HttpClient for Nightscout V1/V2 API (uses api-secret header)
    /// </summary>
    public HttpClient NightscoutClient => _sharedState?.NightscoutClient
        ?? throw new InvalidOperationException("Fixture not initialized");

    /// <summary>
    /// HttpClient for Nightscout V3 API (uses JWT Bearer token)
    /// </summary>
    public HttpClient NightscoutV3Client => _sharedState?.NightscoutV3Client
        ?? throw new InvalidOperationException("Fixture not initialized");

    public HttpClient NocturneClient => _sharedState?.NocturneClient
        ?? throw new InvalidOperationException("Fixture not initialized");

    public NocturneDbContext DbContext => _sharedState?.DbContext
        ?? throw new InvalidOperationException("Fixture not initialized");

    /// <summary>
    /// The JWT token used for V3 API authentication (for debugging)
    /// </summary>
    public string? JwtToken => _sharedState?.NightscoutContainer.JwtToken;

    public async Task InitializeAsync()
    {
        await _initializationSemaphore.WaitAsync();
        try
        {
            if (_sharedState == null)
            {
                using var measurement = TestPerformanceTracker.MeasureTest("ParityTestFixture.Initialize");
                _sharedState = new SharedParityState();
                await _sharedState.InitializeAsync();
            }

            Interlocked.Increment(ref _instanceCount);
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
            var remainingInstances = Interlocked.Decrement(ref _instanceCount);

            if (remainingInstances == 0 && _sharedState != null)
            {
                await _sharedState.DisposeAsync();
                _sharedState = null;
            }
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    /// <summary>
    /// Cleans up test data from both Nightscout and Nocturne.
    /// IMPORTANT: This must complete fully before the next test starts.
    /// </summary>
    public async Task CleanupDataAsync(CancellationToken cancellationToken = default)
    {
        if (_sharedState == null) return;

        // Clean Nocturne (PostgreSQL) first using ExecuteDeleteAsync for bulk deletion
        // This is more reliable than RemoveRange as it bypasses the change tracker
        var db = _sharedState.DbContext;

        // Clear change tracker to ensure no stale entities
        db.ChangeTracker.Clear();

        // Use ExecuteDeleteAsync for efficient bulk deletion that bypasses EF tracking
        await db.Entries.ExecuteDeleteAsync(cancellationToken);
        await db.Treatments.ExecuteDeleteAsync(cancellationToken);
        await db.DeviceStatuses.ExecuteDeleteAsync(cancellationToken);
        await db.Foods.ExecuteDeleteAsync(cancellationToken);
        await db.Profiles.ExecuteDeleteAsync(cancellationToken);
        await db.Settings.ExecuteDeleteAsync(cancellationToken);
        await db.StateSpans.ExecuteDeleteAsync(cancellationToken);
        await db.Activities.ExecuteDeleteAsync(cancellationToken);

        // Clean Nightscout (network calls - may have latency)
        await _sharedState.NightscoutContainer.CleanupDataAsync(cancellationToken);
    }

    /// <summary>
    /// Shared state across all parity tests in the collection
    /// </summary>
    private class SharedParityState : IAsyncDisposable
    {
        private PostgreSqlContainer? _postgresContainer;
        private WebApplicationFactory<Program>? _nocturneFactory;
        private HttpClient? _nightscoutV3Client;

        public NightscoutContainer NightscoutContainer { get; } = new();
        public HttpClient NightscoutClient { get; private set; } = null!;
        public HttpClient NightscoutV3Client => _nightscoutV3Client
            ?? throw new InvalidOperationException("V3 client not initialized - JWT token may have failed to fetch");
        public HttpClient NocturneClient { get; private set; } = null!;
        public NocturneDbContext DbContext { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            // Start Nightscout (includes MongoDB and fetches JWT token)
            await NightscoutContainer.StartAsync();
            NightscoutClient = NightscoutContainer.Client;

            // Create V3 client with JWT Bearer authentication
            if (!string.IsNullOrEmpty(NightscoutContainer.JwtToken))
            {
                _nightscoutV3Client = new HttpClient
                {
                    BaseAddress = new Uri(NightscoutContainer.BaseUrl),
                    Timeout = TimeSpan.FromSeconds(30)
                };
                _nightscoutV3Client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", NightscoutContainer.JwtToken);
                // Nightscout returns tab-separated text by default; we need JSON for parity tests
                _nightscoutV3Client.DefaultRequestHeaders.Add("Accept", "application/json");
            }

            // Start PostgreSQL for Nocturne
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("nocturne_parity")
                .WithUsername("test")
                .WithPassword("test")
                .Build();

            await _postgresContainer.StartAsync();
            var connectionString = _postgresContainer.GetConnectionString();

            // Create Nocturne WebApplicationFactory
            _nocturneFactory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.Sources.Clear();
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DefaultConnection"] = connectionString,
                            ["PostgreSql:ConnectionString"] = connectionString,
                            ["PostgreSql:DatabaseName"] = "nocturne_parity",
                            ["API_SECRET"] = "test-api-secret-12chars",
                            ["NIGHTSCOUT_API_SECRET"] = "test-api-secret-12chars",
                            ["DISPLAY_UNITS"] = "mg/dl",
                            ["Features:EnableExternalConnectors"] = "false",
                            ["Features:EnableRealTimeNotifications"] = "false",
                            ["Environment"] = "Testing",
                            ["Authentication:RequireApiSecret"] = "false"
                        });
                    });

                    builder.ConfigureServices(services =>
                    {
                        // Remove existing DbContext registrations
                        var descriptorsToRemove = services
                            .Where(d =>
                                d.ServiceType == typeof(DbContextOptions<NocturneDbContext>) ||
                                d.ServiceType == typeof(NocturneDbContext))
                            .ToList();

                        foreach (var descriptor in descriptorsToRemove)
                        {
                            services.Remove(descriptor);
                        }

                        // Add PostgreSQL infrastructure
                        services.AddPostgreSqlInfrastructure(connectionString, config =>
                        {
                            config.EnableDetailedErrors = true;
                            config.EnableSensitiveDataLogging = true;
                        });
                    });

                    builder.UseEnvironment("Testing");
                });

            NocturneClient = _nocturneFactory.CreateClient();
            // Set Accept header to match Nightscout client behavior for consistent parity testing
            NocturneClient.DefaultRequestHeaders.Add("Accept", "application/json");

            // Create DbContext for direct database operations
            var options = new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            DbContext = new NocturneDbContext(options);
            await DbContext.Database.EnsureCreatedAsync();
        }

        public async ValueTask DisposeAsync()
        {
            NocturneClient.Dispose();
            _nightscoutV3Client?.Dispose();

            if (DbContext != null)
            {
                await DbContext.Database.EnsureDeletedAsync();
                await DbContext.DisposeAsync();
            }

            _nocturneFactory?.Dispose();

            await NightscoutContainer.DisposeAsync();

            if (_postgresContainer != null)
            {
                await _postgresContainer.StopAsync();
                await _postgresContainer.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Collection definition for parity tests to share the fixture.
/// DisableParallelization ensures tests run sequentially to avoid data contamination
/// since they share the same Nightscout and PostgreSQL instances.
/// </summary>
[CollectionDefinition("Parity", DisableParallelization = true)]
public class ParityTestCollection : ICollectionFixture<ParityTestFixture>
{
}
