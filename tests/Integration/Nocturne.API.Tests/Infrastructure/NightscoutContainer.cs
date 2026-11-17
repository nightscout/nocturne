using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using MongoDB.Bson;
using MongoDB.Driver;

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

    private static readonly string[] DataCollections =
        ["entries", "treatments", "devicestatus", "food", "profile", "settings", "activity"];

    private INetwork? _network;
    private IContainer? _mongoContainer;
    private IContainer? _nightscoutContainer;
    private HttpClient? _httpClient;
    private MongoClient? _mongoClient;

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

    /// <summary>
    /// Nightscout's <c>ENABLE</c>. Nightscout mounts <c>/api/v1/alexa</c> only when <c>alexa</c> is
    /// enabled. It reports this list, followed by the defaults it adds, as <c>settings.enable</c>.
    /// </summary>
    public const string Enable = "careportal basal iob cob bwp cage sage iage bage pump openaps loop alexa";

    /// <summary>
    /// The <c>settings.enable</c> Nightscout reports for <see cref="Enable"/>, given to Nocturne as
    /// <c>Features:Enable</c> so status compares like for like.
    /// </summary>
    public const string ReportedEnable = Enable
        + " treatmentnotify bgnow delta direction timeago devicestatus upbat errorcodes profile bolus dbsize runtimestate ar2";

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
        _mongoContainer = new ContainerBuilder("mongo:7")
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
        _mongoClient = new MongoClient(MongoConnectionString);

        // Build internal MongoDB connection string for Nightscout container
        // Nightscout needs to connect via Docker network, not localhost
        var internalMongoUri = $"mongodb://{MongoNetworkAlias}:27017/nightscout";

        // Start Nightscout connected to MongoDB via internal network
        _nightscoutContainer = new ContainerBuilder(NightscoutImage)
            .WithNetwork(_network)
            .WithPortBinding(NightscoutPort, true)
            .WithEnvironment("MONGODB_URI", internalMongoUri)
            .WithEnvironment("MONGO_CONNECTION", internalMongoUri)
            .WithEnvironment("API_SECRET", ApiSecret)
            .WithEnvironment("DISPLAY_UNITS", "mg/dl")
            .WithEnvironment("ENABLE", Enable)
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

        await WaitUntilLoadedAsync(cancellationToken);

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
    /// <summary>
    /// Waits for Nightscout's <c>runtimeState</c> to reach <c>loaded</c>. It answers status as
    /// soon as it listens, while still <c>booted</c>, and until its first data load it serves
    /// empty in-memory data (summary, properties) and reports the earlier state in every status.
    /// </summary>
    private async Task WaitUntilLoadedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        string? state = null;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await _httpClient!.GetAsync("/api/v1/status.json", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                state = status.RootElement.TryGetProperty("runtimeState", out var s) ? s.GetString() : null;
                if (state == "loaded")
                    return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Nightscout did not reach runtimeState 'loaded' (last seen: {state ?? "none"}).");
    }

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
    /// Empties the collections parity tests write and the cache Nightscout serves v1 reads from.
    /// Authorization subjects and Nightscout's notification state are kept.
    /// </summary>
    /// <remarks>
    /// Nightscout keeps recent entries, treatments and device statuses in a cache that v1 reads are
    /// served from and that every reload merges into what it reads from Mongo, so a document deleted
    /// in Mongo alone is still served. The cache is cleared on every call, even when Mongo held
    /// nothing: a reload that read Mongo before the delete and lands after the clear puts the
    /// documents back. The clear also requests the reload that
    /// <c>ParityTestBase.WaitForNightscoutEmptyAsync</c> waits for.
    /// </remarks>
    public async Task CleanupDataAsync(CancellationToken cancellationToken = default)
    {
        if (_httpClient == null || _mongoClient == null) return;

        var database = _mongoClient.GetDatabase("nightscout");
        await Task.WhenAll(DataCollections.Select(name => database.GetCollection<BsonDocument>(name)
            .DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken)));

        // A v1 remove that names no _id clears the cache of all three collections. It needs a find:
        // treatments.remove reads opts.find._id in its Mongo callback, and without one that throws
        // in the Nightscout process. Treatments, because devicestatus.remove requests no reload.
        using var clear = await _httpClient.DeleteAsync(
            "/api/v1/treatments/?find[created_at][$gte]=1970-01-01T00:00:00.000Z", cancellationToken);
        if (!clear.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Nightscout answered {(int)clear.StatusCode} to the v1 treatments delete that clears its cache: "
                + await clear.Content.ReadAsStringAsync(cancellationToken));
        }

        await AssertCacheServedReadsEmptyAsync(cancellationToken);
    }

    /// <summary>
    /// Throws unless the v1 reads Nightscout serves from its cache return nothing. With
    /// <c>count=1</c> each is served from the cache when it holds anything, else from Mongo.
    /// It cannot see a reload that read Mongo before the delete and lands after the check.
    /// </summary>
    private async Task AssertCacheServedReadsEmptyAsync(CancellationToken cancellationToken)
    {
        string[] paths = ["/api/v1/entries?count=1", "/api/v1/treatments?count=1", "/api/v1/devicestatus?count=1"];
        var served = await Task.WhenAll(paths.Select(async path =>
        {
            using var response = await _httpClient!.GetAsync(path, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Nightscout answered {(int)response.StatusCode} to {path} after cleanup: {body}");
            }

            using var json = JsonDocument.Parse(body);
            return (Path: path, Count: json.RootElement.GetArrayLength());
        }));

        var left = served.Where(s => s.Count > 0).Select(s => s.Path).ToList();
        if (left.Count > 0)
        {
            throw new InvalidOperationException(
                $"Nightscout still serves documents after cleanup from: {string.Join(", ", left)}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        _mongoClient?.Dispose();

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
