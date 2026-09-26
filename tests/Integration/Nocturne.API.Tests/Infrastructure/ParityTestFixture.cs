using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.DevOnly;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
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

    private const string TenantSlug = "parity";

    /// <summary>The API's <c>BASE_DOMAIN</c>; the tenant is <c>{slug}.{BaseDomain}</c>.</summary>
    private const string BaseDomain = "localhost";

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

        // Nocturne first: its records, not the tenant, its members or the API secret's grant.
        await using (var conn = new NpgsqlConnection(_sharedState.SuperuserConnectionString))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE {string.Join(", ", ApiIntegrationTestFixture.RecordTables)} CASCADE";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Clean Nightscout (network calls - may have latency)
        await _sharedState.NightscoutContainer.CleanupDataAsync(cancellationToken);
    }

    /// <summary>
    /// Shared state across all parity tests in the collection
    /// </summary>
    private class SharedParityState : IAsyncDisposable
    {
        private WebApplicationFactory<Nocturne.API.Program>? _nocturneFactory;
        private HttpClient? _nightscoutV3Client;

        public NightscoutContainer NightscoutContainer { get; } = new();
        public HttpClient NightscoutClient { get; private set; } = null!;
        public HttpClient NightscoutV3Client => _nightscoutV3Client
            ?? throw new InvalidOperationException("V3 client not initialized - JWT token may have failed to fetch");
        public HttpClient NocturneClient { get; private set; } = null!;
        public string SuperuserConnectionString { get; private set; } = string.Empty;

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

            // A migrated database on the shared container: the production schema (EnsureCreated
            // on NocturneDbContext alone misses tables such as DataProtectionKeys) and roles.
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("nocturne_parity");
            var connectionString = database.AppConnectionString;

            // Create Nocturne WebApplicationFactory
            _nocturneFactory = new ApiFactory()
                .WithWebHostBuilder(builder =>
                {
                    // Read while the host is being built, before the configuration below applies.
                    builder.UseSetting(DevOnlyEndpoints.EnableVariable, "true");
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.Sources.Clear();
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DefaultConnection"] = connectionString,
                            ["PostgreSql:ConnectionString"] = connectionString,
                            ["PostgreSql:DatabaseName"] = "nocturne_parity",
                            ["INSTANCE_KEY"] = ApiIntegrationTestFixture.InstanceKey,
                            ["BASE_DOMAIN"] = BaseDomain,
                            [DevOnlyEndpoints.EnableVariable] = "true",
                            ["DISPLAY_UNITS"] = "mg/dl",
                            ["Features:EnableExternalConnectors"] = "false",
                            ["Features:EnableRealTimeNotifications"] = "false",
                            ["Environment"] = "Testing",
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
                        services.AddPostgreSqlInfrastructure(connectionString, configuration: null, configure: config =>
                        {
                            config.EnableDetailedErrors = true;
                            config.EnableSensitiveDataLogging = true;
                        });
                    });

                    builder.UseEnvironment("Testing");
                });

            SuperuserConnectionString = database.SuperuserConnectionString;
            await ConfigureTenantLikeNightscoutAsync();

            NocturneClient = _nocturneFactory.CreateClient(
                new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{TenantSlug}.{BaseDomain}") });
            // What NightscoutClient sends: the SHA-1 of the API secret, and a request for JSON.
            NocturneClient.DefaultRequestHeaders.Add("api-secret", NightscoutContainer.ApiSecretHash);
            NocturneClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>
        /// Seeds the tenant the parity tests address and gives it the Nightscout container's
        /// <c>API_SECRET</c>: a full-access legacy-secret grant matched by the SHA-1 Nightscout
        /// clients send. <c>AUTH_DEFAULT_ROLES</c> has no counterpart: a tenant host serves no
        /// anonymous reads, and every parity request carries the secret.
        /// </summary>
        private async Task ConfigureTenantLikeNightscoutAsync()
        {
            using var apex = _nocturneFactory!.CreateClient(
                new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{BaseDomain}") });
            using var seed = await apex.PostAsJsonAsync(
                "/api/v4/dev-only/admin/seed-tenant",
                new DevSeedTenantRequest(TenantSlug, "Parity", "parity-owner"));
            if (!seed.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Seeding tenant '{TenantSlug}' answered {(int)seed.StatusCode}: " + await seed.Content.ReadAsStringAsync());
            }

            var seeded = (await seed.Content.ReadFromJsonAsync<DevSeedTenantResponse>())!;

            await using var conn = new NpgsqlConnection(SuperuserConnectionString);
            await conn.OpenAsync();
            await AuthTestHelpers.SeedApiSecretGrantAsync(
                conn, seeded.TenantId, seeded.SubjectId, NightscoutContainer.ApiSecretHash);
        }

        public async ValueTask DisposeAsync()
        {
            NocturneClient.Dispose();
            _nightscoutV3Client?.Dispose();

            _nocturneFactory?.Dispose();

            await NightscoutContainer.DisposeAsync();

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
