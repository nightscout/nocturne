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
/// The fixture of one parity shard (<see cref="ParityShards"/>): a Nightscout 15.0.3 with its own
/// MongoDB, and a Nocturne host on its own database.
/// </summary>
public class ParityTestFixture : IAsyncLifetime
{
    private ParityStack? _stack;

    private const string TenantSlug = "parity";

    /// <summary>The API's <c>BASE_DOMAIN</c>; the tenant is <c>{slug}.{BaseDomain}</c>.</summary>
    private const string BaseDomain = "localhost";

    /// <summary>
    /// HttpClient for Nightscout V1/V2 API (uses api-secret header)
    /// </summary>
    public HttpClient NightscoutClient => _stack?.NightscoutClient
        ?? throw new InvalidOperationException("Fixture not initialized");

    /// <summary>
    /// HttpClient for Nightscout V3 API (uses JWT Bearer token)
    /// </summary>
    public HttpClient NightscoutV3Client => _stack?.NightscoutV3Client
        ?? throw new InvalidOperationException("Fixture not initialized");

    public HttpClient NocturneClient => _stack?.NocturneClient
        ?? throw new InvalidOperationException("Fixture not initialized");


    /// <summary>
    /// The JWT token used for V3 API authentication (for debugging)
    /// </summary>
    public string? JwtToken => _stack?.NightscoutContainer.JwtToken;

    public async Task InitializeAsync()
    {
        using var measurement = TestPerformanceTracker.MeasureTest("ParityTestFixture.Initialize");
        _stack = new ParityStack();
        await _stack.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_stack != null)
        {
            await _stack.DisposeAsync();
            _stack = null;
        }
    }

    /// <summary>
    /// Cleans up test data from both Nightscout and Nocturne.
    /// IMPORTANT: This must complete fully before the next test starts.
    /// </summary>
    public async Task CleanupDataAsync(CancellationToken cancellationToken = default)
    {
        if (_stack == null) return;

        // Nocturne first: its records, not the tenant, its members or the API secret's grant.
        await using (var conn = new NpgsqlConnection(_stack.SuperuserConnectionString))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE {string.Join(", ", ApiIntegrationTestFixture.RecordTables)} CASCADE";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await ApiIntegrationTestFixture.ClearApiCachesAsync(
            _stack.Services, _stack.TenantId, cancellationToken);

        // Clean Nightscout (network calls - may have latency)
        await _stack.NightscoutContainer.CleanupDataAsync(cancellationToken);
    }

    private class ParityStack : IAsyncDisposable
    {
        private WebApplicationFactory<Nocturne.API.Program>? _nocturneFactory;
        private HttpClient? _nightscoutV3Client;

        public NightscoutContainer NightscoutContainer { get; } = new();
        public HttpClient NightscoutClient { get; private set; } = null!;
        public HttpClient NightscoutV3Client => _nightscoutV3Client
            ?? throw new InvalidOperationException("V3 client not initialized - JWT token may have failed to fetch");
        public HttpClient NocturneClient { get; private set; } = null!;
        public string SuperuserConnectionString { get; private set; } = string.Empty;
        public Guid TenantId { get; private set; }
        public IServiceProvider Services => _nocturneFactory!.Services;

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
                            ["Features:Enable"] = NightscoutContainer.ReportedEnable,
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
                            config.MaxPoolSize = ApiIntegrationTestFixture.MaxPoolSize;
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
            TenantId = seeded.TenantId;

            await using var conn = new NpgsqlConnection(SuperuserConnectionString);
            await conn.OpenAsync();
            await AuthTestHelpers.SeedApiSecretGrantAsync(
                conn, seeded.TenantId, seeded.SubjectId, NightscoutContainer.ApiSecretHash);
        }

        public async ValueTask DisposeAsync()
        {
            // Null after a failed InitializeAsync, which must still stop the containers.
            NocturneClient?.Dispose();
            _nightscoutV3Client?.Dispose();

            _nocturneFactory?.Dispose();

            await NightscoutContainer.DisposeAsync();
        }
    }
}

/// <summary>
/// The shards of the parity suite. Each is an xUnit collection with a
/// <see cref="ParityTestFixture"/> of its own (its own Nightscout, MongoDB, Nocturne host and
/// database), so the shards run in parallel with each other and the rest of the suite, while the
/// tests inside one run one after another, cleaned before each test by <c>ParityTestBase</c>.
/// </summary>
/// <remarks>
/// Most of a shard's time is idle, waiting for Nightscout's reload
/// (<see cref="Parity.ParityTestBase.WaitForNightscoutBodyAsync"/>), so a shard costs a Nightscout,
/// a MongoDB and an API host more than it costs CPU. A class names its shard on itself under the
/// rules of <see cref="ApiIntegrationShards"/>, which <see cref="ParityShardTests"/> holds for these.
/// The shards are balanced by measured duration; <c>tests/README.md</c> says how to measure them.
/// </remarks>
public static class ParityShards
{
    public const string One = "Parity1";
    public const string Two = "Parity2";

    public static readonly IReadOnlyList<string> All = [One, Two];
}

[CollectionDefinition(ParityShards.One)]
public class ParityShard1 : ICollectionFixture<ParityTestFixture>;

[CollectionDefinition(ParityShards.Two)]
public class ParityShard2 : ICollectionFixture<ParityTestFixture>;
