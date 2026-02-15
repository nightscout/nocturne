using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Manages a Nightscout 15.0.3 container with its MongoDB dependency for parity testing.
/// Follows the same lifecycle pattern as SharedContainerState in TestDatabaseFixture.
/// </summary>
public class NightscoutContainer : IAsyncDisposable
{
    private const string NightscoutImage = "nightscout/cgm-remote-monitor:15.0.3";
    private const string ApiSecret = "test-api-secret-12chars";
    private const int NightscoutPort = 1337;
    private const string MongoNetworkAlias = "mongodb";

    private INetwork? _network;
    private IContainer? _mongoContainer;
    private IContainer? _nightscoutContainer;
    private HttpClient? _httpClient;

    public string BaseUrl { get; private set; } = string.Empty;
    public string MongoConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// JWT token for V3 API authentication
    /// </summary>
    public string? JwtToken { get; private set; }

    /// <summary>
    /// Pre-computed SHA1 hash of the API secret for authenticated requests
    /// </summary>
    public string ApiSecretHash { get; } = ComputeSha1Hash(ApiSecret);

    /// <summary>
    /// HttpClient configured with the API secret header for authenticated requests (V1/V2)
    /// </summary>
    public HttpClient Client => _httpClient ?? throw new InvalidOperationException("Container not started");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var measurement = TestPerformanceTracker.MeasureTest("NightscoutContainer.Start");

        // Create a shared network for MongoDB and Nightscout containers
        _network = new NetworkBuilder()
            .WithName($"nightscout-test-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync(cancellationToken);

        // Start MongoDB first (Nightscout's backend)
        // Use plain mongo container without authentication (MongoDbBuilder adds auth which breaks Nightscout)
        _mongoContainer = new ContainerBuilder()
            .WithImage("mongo:7")
            .WithNetwork(_network)
            .WithNetworkAliases(MongoNetworkAlias)
            .WithPortBinding(27017, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("mongosh", "--eval", "db.runCommand('ping').ok"))
            .Build();

        await _mongoContainer.StartAsync(cancellationToken);

        // Store host-accessible connection string for external access
        var mongoHost = _mongoContainer.Hostname;
        var mongoPort = _mongoContainer.GetMappedPublicPort(27017);
        MongoConnectionString = $"mongodb://{mongoHost}:{mongoPort}/nightscout";

        // Build internal MongoDB connection string for Nightscout container
        // Nightscout needs to connect via Docker network, not localhost
        var internalMongoUri = $"mongodb://{MongoNetworkAlias}:27017/nightscout";

        // Start Nightscout connected to MongoDB via internal network
        _nightscoutContainer = new ContainerBuilder()
            .WithImage(NightscoutImage)
            .WithNetwork(_network)
            .WithPortBinding(NightscoutPort, true)
            .WithEnvironment("MONGODB_URI", internalMongoUri)
            .WithEnvironment("MONGO_CONNECTION", internalMongoUri)
            .WithEnvironment("API_SECRET", ApiSecret)
            .WithEnvironment("DISPLAY_UNITS", "mg/dl")
            .WithEnvironment("ENABLE", "careportal basal iob cob bwp cage sage iage bage pump openaps loop")
            .WithEnvironment("AUTH_DEFAULT_ROLES", "readable")
            .WithEnvironment("INSECURE_USE_HTTP", "true")
            .WithEnvironment("SECURE_HSTS_HEADER", "false")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPath("/api/v1/status.json")
                    .ForPort(NightscoutPort)))
            .Build();

        await _nightscoutContainer.StartAsync(cancellationToken);

        var host = _nightscoutContainer.Hostname;
        var port = _nightscoutContainer.GetMappedPublicPort(NightscoutPort);
        BaseUrl = $"http://{host}:{port}";

        // Create HttpClient with API secret header for V1/V2
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("api-secret", ApiSecretHash);
        // Nightscout returns tab-separated text by default; we need JSON for parity tests
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        // Fetch JWT token for V3 API authentication
        await FetchJwtTokenAsync(cancellationToken);
    }

    /// <summary>
    /// Fetches a JWT token from the Nightscout authorization endpoint for V3 API access.
    /// Flow:
    /// 1. Create a test subject with admin role
    /// 2. Get the access token from the created subject
    /// 3. Request a JWT using the access token
    /// </summary>
    private async Task FetchJwtTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Step 1: Create a test subject with admin role
            var createSubjectResponse = await _httpClient!.PostAsJsonAsync(
                "/api/v2/authorization/subjects",
                new { name = "parity-test", roles = new[] { "admin" } },
                cancellationToken);

            if (!createSubjectResponse.IsSuccessStatusCode)
                return;

            // Step 2: Get the created subject to retrieve the access token
            var getSubjectsResponse = await _httpClient!.GetAsync(
                "/api/v2/authorization/subjects",
                cancellationToken);

            if (!getSubjectsResponse.IsSuccessStatusCode)
                return;

            var subjectsContent = await getSubjectsResponse.Content.ReadAsStringAsync(cancellationToken);
            using var subjectsDoc = JsonDocument.Parse(subjectsContent);

            string? accessToken = null;
            foreach (var subject in subjectsDoc.RootElement.EnumerateArray())
            {
                if (subject.TryGetProperty("name", out var nameElement) &&
                    nameElement.GetString() == "parity-test" &&
                    subject.TryGetProperty("accessToken", out var tokenElement))
                {
                    accessToken = tokenElement.GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(accessToken))
                return;

            // Step 3: Request a JWT using the access token
            var jwtResponse = await _httpClient.GetAsync(
                $"/api/v2/authorization/request/{accessToken}",
                cancellationToken);

            if (!jwtResponse.IsSuccessStatusCode)
                return;

            var jwtContent = await jwtResponse.Content.ReadAsStringAsync(cancellationToken);
            using var jwtDoc = JsonDocument.Parse(jwtContent);

            if (jwtDoc.RootElement.TryGetProperty("token", out var jwtTokenElement))
            {
                JwtToken = jwtTokenElement.GetString();
            }
        }
        catch
        {
            // JWT token fetch failed - V3 tests will fail with 401
            // This is acceptable as it makes the issue visible in test output
        }
    }

    /// <summary>
    /// Clears all data from Nightscout collections
    /// </summary>
    public async Task CleanupDataAsync(CancellationToken cancellationToken = default)
    {
        if (_httpClient == null) return;

        // Create a JWT-authenticated client for V3 cleanup operations
        using var v3Client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl!),
            Timeout = TimeSpan.FromSeconds(30)
        };
        v3Client.DefaultRequestHeaders.Add("Accept", "application/json");
        if (!string.IsNullOrEmpty(JwtToken))
        {
            v3Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", JwtToken);
        }

        // Use V3 API for cleanup - it supports deletion by identifier
        // Include all collections that can have test data
        var v3Collections = new[] { "entries", "treatments", "devicestatus", "food", "profile", "settings" };
        foreach (var collection in v3Collections)
        {
            try
            {
                // Keep fetching and deleting until no more documents
                int deleted;
                int maxIterations = 10; // Prevent infinite loops
                int iteration = 0;
                do
                {
                    deleted = 0;
                    iteration++;
                    var getResponse = await v3Client.GetAsync(
                        $"/api/v3/{collection}?limit=100",
                        cancellationToken);

                    if (!getResponse.IsSuccessStatusCode) break;

                    var content = await getResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = System.Text.Json.JsonDocument.Parse(content);

                    if (doc.RootElement.TryGetProperty("result", out var results))
                    {
                        foreach (var item in results.EnumerateArray())
                        {
                            // Try both 'identifier' and '_id' field names
                            string? identifier = null;
                            if (item.TryGetProperty("identifier", out var idProp))
                            {
                                identifier = idProp.GetString();
                            }
                            else if (item.TryGetProperty("_id", out var mongoIdProp))
                            {
                                identifier = mongoIdProp.GetString();
                            }

                            if (!string.IsNullOrEmpty(identifier))
                            {
                                var deleteResponse = await v3Client.DeleteAsync(
                                    $"/api/v3/{collection}/{identifier}?permanent=true",
                                    cancellationToken);
                                if (deleteResponse.IsSuccessStatusCode)
                                    deleted++;
                            }
                        }
                    }
                } while (deleted > 0 && iteration < maxIterations);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // Small delay to ensure deletions are fully processed
        await Task.Delay(50, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();

        if (_nightscoutContainer != null)
        {
            await _nightscoutContainer.StopAsync();
            await _nightscoutContainer.DisposeAsync();
        }

        if (_mongoContainer != null)
        {
            await _mongoContainer.StopAsync();
            await _mongoContainer.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DeleteAsync();
            await _network.DisposeAsync();
        }
    }

    private static string ComputeSha1Hash(string input)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
