using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Service for managing MongoDB-to-Nocturne migration jobs. Supports starting, monitoring,
/// and cancelling migrations, as well as testing source connections and retrieving migration history.
/// </summary>
public interface IMigrationJobService
{
    Task<MigrationJobInfo> StartMigrationAsync(
        StartMigrationRequest request,
        CancellationToken ct = default
    );

    /// <exception cref="KeyNotFoundException">Thrown when the migration job is not found.</exception>
    Task<MigrationJobStatus> GetStatusAsync(Guid jobId);

    /// <exception cref="KeyNotFoundException">Thrown when the migration job is not found.</exception>
    Task CancelAsync(Guid jobId);
    Task<IReadOnlyList<MigrationJobInfo>> GetHistoryAsync();
    Task<TestMigrationConnectionResult> TestConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct = default
    );
    PendingMigrationConfig GetPendingConfig();
    Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(CancellationToken ct = default);
}

/// <summary>
/// Implements <see cref="IMigrationJobService"/>. Runs migration jobs as background
/// <see cref="Task"/> instances, tracked in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by job ID. Each job streams MongoDB collections into the Nocturne EF Core database in
/// configurable batches.
/// </summary>
/// <seealso cref="IMigrationJobService"/>
public class MigrationJobService : IMigrationJobService
{
    private readonly ILogger<MigrationJobService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<Guid, MigrationJob> _jobs = new();
    private readonly List<MigrationJobInfo> _history = [];
    private readonly object _historyLock = new();

    public MigrationJobService(
        ILogger<MigrationJobService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public async Task<MigrationJobInfo> StartMigrationAsync(
        StartMigrationRequest request,
        CancellationToken ct = default
    )
    {
        var jobId = Guid.CreateVersion7();
        var sourceDesc =
            request.Mode == MigrationMode.Api
                ? request.NightscoutUrl
                : $"MongoDB: {request.MongoDatabaseName}";

        var jobInfo = new MigrationJobInfo
        {
            Id = jobId,
            Mode = request.Mode,
            CreatedAt = DateTime.UtcNow,
            SourceDescription = sourceDesc,
        };

        var job = new MigrationJob(jobId, request, jobInfo, _logger, _serviceProvider);
        _jobs[jobId] = job;

        lock (_historyLock)
        {
            _history.Add(jobInfo);
        }

        // Start migration in background
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await job.ExecuteAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Migration job {JobId} failed", jobId);
                }
            },
            ct
        );

        _logger.LogInformation(
            "Started migration job {JobId} in {Mode} mode from {Source}",
            jobId,
            request.Mode,
            sourceDesc
        );

        return jobInfo;
    }

    public Task<MigrationJobStatus> GetStatusAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            return Task.FromResult(job.GetStatus());
        }

        throw new KeyNotFoundException($"Migration job {jobId} not found");
    }

    public Task CancelAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Cancel();
            _logger.LogInformation("Cancelled migration job {JobId}", jobId);
            return Task.CompletedTask;
        }

        throw new KeyNotFoundException($"Migration job {jobId} not found");
    }

    public Task<IReadOnlyList<MigrationJobInfo>> GetHistoryAsync()
    {
        lock (_historyLock)
        {
            return Task.FromResult<IReadOnlyList<MigrationJobInfo>>(_history.ToList());
        }
    }

    public async Task<TestMigrationConnectionResult> TestConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct = default
    )
    {
        try
        {
            if (request.Mode == MigrationMode.Api)
            {
                return await TestApiConnectionAsync(request, ct);
            }
            else
            {
                return await TestMongoConnectionAsync(request, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test migration connection");
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private async Task<TestMigrationConnectionResult> TestApiConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(request.NightscoutUrl))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = "Nightscout URL is required",
            };
        }

        using var scope = _serviceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(request.NightscoutUrl.TrimEnd('/'));

        // Add API secret header if provided (Nightscout expects the SHA1 hash)
        if (!string.IsNullOrEmpty(request.NightscoutApiSecret))
        {
            httpClient.DefaultRequestHeaders.Add("api-secret", MigrationJob.HashApiSecret(request.NightscoutApiSecret));
        }

        try
        {
            var response = await httpClient.GetAsync("/api/v1/status", ct);
            if (!response.IsSuccessStatusCode)
            {
                return new TestMigrationConnectionResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Failed to connect: {response.StatusCode}",
                };
            }

            return new TestMigrationConnectionResult
            {
                IsSuccess = true,
                SiteName = request.NightscoutUrl,
                AvailableCollections = ["subjects", "entries", "treatments", "profile", "devicestatus", "food", "activity"],
            };
        }
        catch (HttpRequestException ex)
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = $"Connection failed: {ex.Message}",
            };
        }
    }

    private async Task<TestMigrationConnectionResult> TestMongoConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(request.MongoConnectionString))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = "MongoDB connection string is required",
            };
        }

        if (string.IsNullOrEmpty(request.MongoDatabaseName))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = "MongoDB database name is required",
            };
        }

        var client = new MongoClient(request.MongoConnectionString);
        var database = client.GetDatabase(request.MongoDatabaseName);

        // List collections
        var collections = await database.ListCollectionNamesAsync(cancellationToken: ct);
        var collectionList = await collections.ToListAsync(ct);

        // Get counts for main collections
        long entryCount = 0;
        long treatmentCount = 0;

        if (collectionList.Contains("entries"))
        {
            var entriesCollection = database.GetCollection<BsonDocument>("entries");
            entryCount = await entriesCollection.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct
            );
        }

        if (collectionList.Contains("treatments"))
        {
            var treatmentsCollection = database.GetCollection<BsonDocument>("treatments");
            treatmentCount = await treatmentsCollection.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct
            );
        }

        return new TestMigrationConnectionResult
        {
            IsSuccess = true,
            SiteName = request.MongoDatabaseName,
            EntryCount = entryCount,
            TreatmentCount = treatmentCount,
            AvailableCollections = collectionList,
        };
    }

    public PendingMigrationConfig GetPendingConfig()
    {
        var migrationMode = _configuration["MIGRATION_MODE"];

        if (string.IsNullOrEmpty(migrationMode))
        {
            return new PendingMigrationConfig { HasPendingConfig = false };
        }

        var mode = migrationMode.Equals("MongoDb", StringComparison.OrdinalIgnoreCase)
            ? MigrationMode.MongoDb
            : MigrationMode.Api;

        return new PendingMigrationConfig
        {
            HasPendingConfig = true,
            Mode = mode,
            NightscoutUrl = _configuration["MIGRATION_NS_URL"],
            HasApiSecret = !string.IsNullOrEmpty(_configuration["MIGRATION_NS_API_SECRET"]),
            HasMongoConnectionString = !string.IsNullOrEmpty(
                _configuration["MIGRATION_MONGO_CONNECTION_STRING"]
            ),
            MongoDatabaseName = _configuration["MIGRATION_MONGO_DATABASE_NAME"],
        };
    }

    public async Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(
        CancellationToken ct = default
    )
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var sources = await dbContext
            .MigrationSources.OrderByDescending(s => s.LastMigrationAt ?? s.CreatedAt)
            .Select(s => new MigrationSourceDto
            {
                Id = s.Id,
                Mode = s.Mode == "MongoDb" ? MigrationMode.MongoDb : MigrationMode.Api,
                NightscoutUrl = s.NightscoutUrl,
                MongoDatabaseName = s.MongoDatabaseName,
                LastMigrationAt = s.LastMigrationAt,
                LastMigratedDataTimestamp = s.LastMigratedDataTimestamp,
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync(ct);

        return sources;
    }
}

/// <summary>
/// Represents a running migration job
/// </summary>
internal class MigrationJob
{
    private readonly Guid _id;
    private readonly StartMigrationRequest _request;
    private readonly MigrationJobInfo _info;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cts = new();
    private MigrationJobState _state = MigrationJobState.Pending;
    private string? _currentOperation;
    private string? _errorMessage;
    private double _progressPercentage;
    private DateTime _startedAt;
    private DateTime? _completedAt;
    private readonly ConcurrentDictionary<string, CollectionProgress> _collectionProgress = new();
    private static readonly System.Text.Json.JsonSerializerOptions s_caseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    public MigrationJob(
        Guid id,
        StartMigrationRequest request,
        MigrationJobInfo info,
        ILogger logger,
        IServiceProvider serviceProvider
    )
    {
        _id = id;
        _request = request;
        _info = info;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public MigrationJobStatus GetStatus() =>
        new()
        {
            JobId = _id,
            State = _state,
            ProgressPercentage = _progressPercentage,
            CurrentOperation = _currentOperation,
            ErrorMessage = _errorMessage,
            StartedAt = _startedAt,
            CompletedAt = _completedAt,
            CollectionProgress = _collectionProgress.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public void Cancel()
    {
        _cts.Cancel();
        _state = MigrationJobState.Cancelled;
    }

    public async Task ExecuteAsync(CancellationToken externalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token,
            externalCt
        );
        var ct = linkedCts.Token;

        _startedAt = DateTime.UtcNow;
        _state = MigrationJobState.Running;

        try
        {
            if (_request.Mode == MigrationMode.Api)
            {
                await ExecuteApiMigrationAsync(ct);
            }
            else
            {
                await ExecuteMongoMigrationAsync(ct);
            }

            _state = MigrationJobState.Completed;
            _progressPercentage = 100;
            _completedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            _state = MigrationJobState.Cancelled;
            _completedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _state = MigrationJobState.Failed;
            _errorMessage =
                ex.InnerException != null
                    ? $"{ex.Message} Inner: {ex.InnerException.Message}"
                    : ex.Message;
            _completedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Migration job {JobId} failed", _id);
        }
    }

    private long _totalDocumentsAllCollections;
    private long _migratedDocumentsAllCollections; // computed by UpdateOverallProgress

    private async Task ExecuteApiMigrationAsync(CancellationToken ct)
    {
        _currentOperation = "Connecting to Nightscout";

        using var scope = _serviceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(_request.NightscoutUrl!.TrimEnd('/'));

        // Add API secret header if provided (Nightscout expects the SHA1 hash)
        if (!string.IsNullOrEmpty(_request.NightscoutApiSecret))
        {
            httpClient.DefaultRequestHeaders.Add("api-secret", HashApiSecret(_request.NightscoutApiSecret));
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // Build the list of collections to migrate
        var allCollections = new (string name, Func<HttpClient, NocturneDbContext, CancellationToken, Task> migrate)[]
        {
            ("subjects", MigrateSubjectsViaApiAsync),
            ("entries", MigrateEntriesViaApiAsync),
            ("treatments", MigrateTreatmentsViaApiAsync),
            ("devicestatus", MigrateDeviceStatusViaApiAsync),
            ("profile", MigrateProfilesViaApiAsync),
            ("food", MigrateFoodViaApiAsync),
            ("activity", MigrateActivityViaApiAsync),
        };

        var collectionsToMigrate = allCollections
            .Where(c => _request.Collections.Count == 0 || _request.Collections.Contains(c.name))
            .ToList();

        // Fetch counts upfront so we can show real X / Y progress
        _currentOperation = "Counting records";
        _totalDocumentsAllCollections = 0;

        foreach (var (name, _) in collectionsToMigrate)
        {
            var count = await FetchCollectionCountAsync(httpClient, name, ct);
            _collectionProgress[name] = new CollectionProgress
            {
                CollectionName = name,
                TotalDocuments = count,
                DocumentsMigrated = 0,
                DocumentsFailed = 0,
                IsComplete = false,
            };
            _totalDocumentsAllCollections += count;
        }

        foreach (var (name, migrate) in collectionsToMigrate)
        {
            await migrate(httpClient, dbContext, ct);
        }
    }

    /// <summary>
    /// Fetches the document count for a collection via the Nightscout count API.
    /// Collections that don't support the count endpoint return 0.
    /// </summary>
    private async Task<long> FetchCollectionCountAsync(
        HttpClient httpClient, string collectionName, CancellationToken ct)
    {
        // Only entries, treatments, devicestatus support the count endpoint
        var countableCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "entries", "treatments", "devicestatus" };

        if (!countableCollections.Contains(collectionName))
            return 0;

        try
        {
            var response = await httpClient.GetAsync($"/api/v1/count/{collectionName}/where", ct);
            if (!response.IsSuccessStatusCode)
                return 0;

            var content = await response.Content.ReadAsStringAsync(ct);
            // Nightscout returns [{"_id": null, "count": N}]
            var results = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(content);
            if (results is { Length: > 0 })
            {
                return results[0].TryGetProperty("count", out var countProp)
                    ? countProp.GetInt64()
                    : 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch count for {Collection}, continuing without total", collectionName);
        }

        return 0;
    }

    /// <summary>
    /// Updates _totalDocumentsAllCollections by summing TotalDocuments across
    /// all tracked collections, then computes _progressPercentage.
    /// This handles both the upfront-count case and the fallback case where
    /// totals are only known after each collection is fetched.
    /// </summary>
    private void UpdateOverallProgress()
    {
        _totalDocumentsAllCollections = _collectionProgress.Values.Sum(c => c.TotalDocuments);
        _migratedDocumentsAllCollections = _collectionProgress.Values.Sum(c => c.DocumentsMigrated);

        if (_totalDocumentsAllCollections > 0)
        {
            _progressPercentage = (double)_migratedDocumentsAllCollections / _totalDocumentsAllCollections * 100;
        }
    }

    private void UpdateCollectionProgress(string collectionName, long totalDocuments, long migrated, long failed, bool isComplete)
    {
        _collectionProgress[collectionName] = new CollectionProgress
        {
            CollectionName = collectionName,
            TotalDocuments = totalDocuments,
            DocumentsMigrated = migrated,
            DocumentsFailed = failed,
            IsComplete = isComplete,
        };
    }

    private async Task MigrateEntriesViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        _currentOperation = "Migrating entries";
        const string collectionName = "entries";
        var knownTotal = _collectionProgress.TryGetValue(collectionName, out var existing)
            ? existing.TotalDocuments : 0;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        DateTime? currentTo = null;
        const int pageSize = 10000;

        using var scope = _serviceProvider.CreateScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<IEntryDecomposer>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"/api/v1/entries.json?count={pageSize}";
            if (currentTo.HasValue)
            {
                var toMs = new DateTimeOffset(currentTo.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
                url += $"&find[date][$lte]={toMs}";
            }

            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch entries: {StatusCode}", response.StatusCode);
                break;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var entries = System.Text.Json.JsonSerializer.Deserialize<Entry[]>(content) ?? [];

            if (entries.Length == 0) break;

            try
            {
                await decomposer.DecomposeBatchAsync(entries, ct);
                totalMigrated += entries.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose entries page");
                totalFailed += entries.Length;
            }

            UpdateCollectionProgress(collectionName,
                Math.Max(knownTotal, totalMigrated + totalFailed),
                totalMigrated, totalFailed, false);
            UpdateOverallProgress();

            if (entries.Length < pageSize) break;

            var oldestMs = entries.Min(e => e.Mills);
            if (oldestMs <= 0) break;

            var oldestDate = DateTimeOffset.FromUnixTimeMilliseconds(oldestMs).UtcDateTime;
            if (currentTo.HasValue && oldestDate >= currentTo.Value) break;
            currentTo = oldestDate.AddMilliseconds(-1);
        }

        UpdateCollectionProgress(collectionName, Math.Max(knownTotal, totalMigrated + totalFailed),
            totalMigrated, totalFailed, true);
        UpdateOverallProgress();
        _logger.LogInformation("Migrated {Count} entries via API", totalMigrated);
    }

    private async Task MigrateTreatmentsViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        _currentOperation = "Migrating treatments";
        const string collectionName = "treatments";
        var knownTotal = _collectionProgress.TryGetValue(collectionName, out var existing)
            ? existing.TotalDocuments : 0;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        DateTime? currentTo = null;
        const int pageSize = 10000;

        using var scope = _serviceProvider.CreateScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<ITreatmentDecomposer>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"/api/v1/treatments.json?count={pageSize}";
            if (currentTo.HasValue)
                url += $"&find[created_at][$lte]={currentTo.Value.ToUniversalTime():o}";

            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch treatments: {StatusCode}", response.StatusCode);
                break;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var treatments = System.Text.Json.JsonSerializer.Deserialize<Treatment[]>(content) ?? [];

            if (treatments.Length == 0) break;

            try
            {
                await decomposer.DecomposeBatchAsync(treatments, ct);
                totalMigrated += treatments.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose treatments page");
                totalFailed += treatments.Length;
            }

            UpdateCollectionProgress(collectionName,
                Math.Max(knownTotal, totalMigrated + totalFailed),
                totalMigrated, totalFailed, false);
            UpdateOverallProgress();

            if (treatments.Length < pageSize) break;

            var oldestDate = treatments
                .Select(t => DateTimeOffset.TryParse(t.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue) break;
            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value) break;
            currentTo = oldestDate.Value.AddMilliseconds(-1);
        }

        UpdateCollectionProgress(collectionName, Math.Max(knownTotal, totalMigrated + totalFailed),
            totalMigrated, totalFailed, true);
        UpdateOverallProgress();
        _logger.LogInformation("Migrated {Count} treatments via API", totalMigrated);
    }

    private async Task MigrateDeviceStatusViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        _currentOperation = "Migrating device statuses";
        const string collectionName = "devicestatus";
        var knownTotal = _collectionProgress.TryGetValue(collectionName, out var existing)
            ? existing.TotalDocuments : 0;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        DateTime? currentTo = null;
        const int pageSize = 10000;

        using var scope = _serviceProvider.CreateScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<IDeviceStatusDecomposer>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"/api/v1/devicestatus.json?count={pageSize}";
            if (currentTo.HasValue)
                url += $"&find[created_at][$lte]={currentTo.Value.ToUniversalTime():o}";

            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch device statuses: {StatusCode}", response.StatusCode);
                break;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var statuses = System.Text.Json.JsonSerializer.Deserialize<DeviceStatus[]>(content) ?? [];

            if (statuses.Length == 0) break;

            try
            {
                await decomposer.DecomposeBatchAsync(statuses, ct);
                totalMigrated += statuses.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose device status page");
                totalFailed += statuses.Length;
            }

            UpdateCollectionProgress(collectionName,
                Math.Max(knownTotal, totalMigrated + totalFailed),
                totalMigrated, totalFailed, false);
            UpdateOverallProgress();

            if (statuses.Length < pageSize) break;

            var oldestDate = statuses
                .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue) break;
            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value) break;
            currentTo = oldestDate.Value.AddMilliseconds(-1);
        }

        UpdateCollectionProgress(collectionName, Math.Max(knownTotal, totalMigrated + totalFailed),
            totalMigrated, totalFailed, true);
        UpdateOverallProgress();
        _logger.LogInformation("Migrated {Count} device statuses via API", totalMigrated);
    }

    private async Task MigrateProfilesViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        _currentOperation = "Migrating profiles";
        var collectionName = "profile";

        var totalMigrated = 0L;
        var totalFailed = 0L;

        try
        {
            var response = await httpClient.GetAsync("/api/v1/profile.json", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch profiles: {StatusCode}", response.StatusCode);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var profiles = System.Text.Json.JsonSerializer.Deserialize<Profile[]>(content) ?? [];

            UpdateCollectionProgress(collectionName, profiles.Length, 0, 0, false);
            UpdateOverallProgress();

            using var scope = _serviceProvider.CreateScope();
            var decomposer = scope.ServiceProvider.GetRequiredService<Nocturne.Core.Contracts.V4.IProfileDecomposer>();

            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (string.IsNullOrEmpty(profile.Id))
                    {
                        profile.Id = Guid.CreateVersion7().ToString();
                    }

                    await decomposer.DecomposeAsync(profile, ct);
                    totalMigrated++;
                    UpdateCollectionProgress(collectionName, profiles.Length, totalMigrated, totalFailed, false);
                    UpdateOverallProgress();
                }
                catch
                {
                    totalFailed++;
                }
            }

            UpdateCollectionProgress(collectionName, profiles.Length, totalMigrated, totalFailed, true);
            UpdateOverallProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating profiles via API");
        }

        _logger.LogInformation("Migrated {Count} profiles via API", totalMigrated);
    }

    private async Task MigrateFoodViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        _currentOperation = "Migrating food";
        const string collectionName = "food";
        var knownTotal = _collectionProgress.TryGetValue(collectionName, out var existing)
            ? existing.TotalDocuments : 0;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var totalSkipped = 0;
        const int pageSize = 10000;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var url = $"/api/v1/food.json?count={pageSize}&skip={totalSkipped}";

                var response = await httpClient.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch food: {StatusCode}", response.StatusCode);
                    break;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                var foods = System.Text.Json.JsonSerializer.Deserialize<Food[]>(content) ?? [];

                if (foods.Length == 0) break;

                foreach (var food in foods)
                {
                    try
                    {
                        var exists = await dbContext.Foods.AnyAsync(
                            f => f.Name == (food.Name ?? "") && f.Type == (food.Type ?? "food"),
                            ct
                        );

                        if (!exists)
                        {
                            dbContext.Foods.Add(
                                new Infrastructure.Data.Entities.FoodEntity
                                {
                                    Id = Guid.CreateVersion7(),
                                    Type = food.Type ?? "food",
                                    Category = food.Category ?? "",
                                    Subcategory = food.Subcategory ?? "",
                                    Name = food.Name ?? "",
                                    Portion = food.Portion,
                                    Carbs = food.Carbs,
                                    Fat = food.Fat,
                                    Protein = food.Protein,
                                    Energy = food.Energy,
                                    Gi = (Infrastructure.Data.Entities.GlycemicIndex)(food.Gi > 0 ? food.Gi : 2),
                                    Unit = food.Unit ?? "g",
                                    Foods = food.Foods != null ? System.Text.Json.JsonSerializer.Serialize(food.Foods) : null,
                                    HideAfterUse = food.HideAfterUse,
                                    Hidden = food.Hidden,
                                    Position = food.Position,
                                }
                            );
                        }
                        totalMigrated++;
                    }
                    catch
                    {
                        totalFailed++;
                    }
                }

                await dbContext.SaveChangesAsync(ct);
                totalSkipped += foods.Length;

                UpdateCollectionProgress(collectionName,
                    Math.Max(knownTotal, totalSkipped),
                    totalMigrated, totalFailed, false);
                UpdateOverallProgress();

                if (foods.Length < pageSize) break;
            }

            UpdateCollectionProgress(collectionName, Math.Max(knownTotal, totalMigrated + totalFailed),
                totalMigrated, totalFailed, true);
            UpdateOverallProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating food via API");
        }

        _logger.LogInformation("Migrated {Count} food items via API", totalMigrated);
    }

    private async Task MigrateActivityViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        _currentOperation = "Migrating activities";
        const string collectionName = "activity";
        var knownTotal = _collectionProgress.TryGetValue(collectionName, out var existing)
            ? existing.TotalDocuments : 0;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        DateTime? currentTo = null;
        const int pageSize = 10000;

        using var scope = _serviceProvider.CreateScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<IActivityDecomposer>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"/api/v1/activity.json?count={pageSize}";
            if (currentTo.HasValue)
                url += $"&find[created_at][$lte]={currentTo.Value.ToUniversalTime():o}";

            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch activities: {StatusCode}", response.StatusCode);
                break;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var activities = System.Text.Json.JsonSerializer.Deserialize<Activity[]>(content) ?? [];

            if (activities.Length == 0) break;

            try
            {
                await decomposer.DecomposeBatchAsync(activities, ct);
                totalMigrated += activities.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose activity page");
                totalFailed += activities.Length;
            }

            UpdateCollectionProgress(collectionName,
                Math.Max(knownTotal, totalMigrated + totalFailed),
                totalMigrated, totalFailed, false);
            UpdateOverallProgress();

            if (activities.Length < pageSize) break;

            var oldestDate = activities
                .Select(a => DateTimeOffset.TryParse(a.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue) break;
            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value) break;
            currentTo = oldestDate.Value.AddMilliseconds(-1);
        }

        UpdateCollectionProgress(collectionName, Math.Max(knownTotal, totalMigrated + totalFailed),
            totalMigrated, totalFailed, true);
        UpdateOverallProgress();
        _logger.LogInformation("Migrated {Count} activities via API", totalMigrated);
    }

    private async Task ExecuteMongoMigrationAsync(CancellationToken ct)
    {
        _currentOperation = "Connecting to MongoDB";

        var client = new MongoClient(_request.MongoConnectionString);
        var database = client.GetDatabase(_request.MongoDatabaseName);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // List available collections
        var collections = await database.ListCollectionNamesAsync(cancellationToken: ct);
        var collectionList = await collections.ToListAsync(ct);

        // Filter to requested collections
        var collectionsToMigrate =
            _request.Collections.Count > 0
                ? collectionList.Where(c => _request.Collections.Contains(c)).ToList()
                : collectionList
                    .Where(c => c is "entries" or "treatments" or "devicestatus" or "profile" or "food" or "activity")
                    .ToList();

        var totalCollections = collectionsToMigrate.Count;
        var processedCollections = 0;

        foreach (var collectionName in collectionsToMigrate)
        {
            ct.ThrowIfCancellationRequested();

            _currentOperation = $"Migrating {collectionName}";

            await MigrateMongoCollectionAsync(database, collectionName, dbContext, ct);

            processedCollections++;
            _progressPercentage = (double)processedCollections / totalCollections * 100;
        }
    }

    private async Task MigrateMongoCollectionAsync(
        IMongoDatabase database,
        string collectionName,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var totalDocs = await collection.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty,
            cancellationToken: ct
        );

        _collectionProgress[collectionName] = new CollectionProgress
        {
            CollectionName = collectionName,
            TotalDocuments = totalDocs,
            DocumentsMigrated = 0,
            DocumentsFailed = 0,
            IsComplete = false,
        };

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var batchSize = 1000;

        var findOptions = new FindOptions<BsonDocument> { BatchSize = batchSize };
        var cursor = await collection.FindAsync(
            FilterDefinition<BsonDocument>.Empty,
            findOptions,
            ct
        );

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                try
                {
                    await TransformAndSaveDocumentAsync(collectionName, doc, dbContext, ct);
                    totalMigrated++;
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    _logger.LogWarning(
                        ex,
                        "Failed to migrate document in {Collection}",
                        collectionName
                    );
                }
            }

            await dbContext.SaveChangesAsync(ct);

            _collectionProgress[collectionName] = new CollectionProgress
            {
                CollectionName = collectionName,
                TotalDocuments = totalDocs,
                DocumentsMigrated = totalMigrated,
                DocumentsFailed = totalFailed,
                IsComplete = false,
            };
        }

        _collectionProgress[collectionName] = _collectionProgress[collectionName] with
        {
            IsComplete = true,
        };

        _logger.LogInformation(
            "Migrated {Count}/{Total} documents from {Collection}",
            totalMigrated,
            totalDocs,
            collectionName
        );
    }

    private async Task TransformAndSaveDocumentAsync(
        string collectionName,
        BsonDocument doc,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        switch (collectionName)
        {
            case "treatments":
                await TransformTreatmentAsync(doc, dbContext, ct);
                break;
            case "devicestatus":
                await TransformDeviceStatusAsync(doc, dbContext, ct);
                break;
            case "profile":
                await TransformProfileAsync(doc, dbContext, ct);
                break;
            case "food":
                await TransformFoodAsync(doc, dbContext, ct);
                break;
            default:
                _logger.LogDebug("Skipping unsupported collection: {Collection}", collectionName);
                break;
        }
    }

    private Task TransformTreatmentAsync(
        BsonDocument doc,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        // MongoDB BSON treatment decomposition is not yet implemented (MongoDB mode is out of scope).
        // The API migration path handles treatments via ITreatmentDecomposer.DecomposeBatchAsync.
        return Task.CompletedTask;
    }

    private async Task TransformDeviceStatusAsync(
        BsonDocument doc,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        // Convert BSON to JSON, then deserialize to DeviceStatus domain model and decompose
        var jsonWriterSettings = new MongoDB.Bson.IO.JsonWriterSettings
        {
            OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson
        };
        var json = doc.ToJson(jsonWriterSettings);
        var status = System.Text.Json.JsonSerializer.Deserialize<DeviceStatus>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (status == null)
            return;

        // Set the original ID from MongoDB _id
        if (doc.Contains("_id"))
            status.Id = doc["_id"].AsObjectId.ToString();

        using var scope = _serviceProvider.CreateScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<Core.Contracts.V4.IDeviceStatusDecomposer>();
        await decomposer.DecomposeAsync(status, ct);
    }

    private async Task TransformProfileAsync(
        BsonDocument doc,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        var mills =
            doc.Contains("mills") ? doc["mills"].ToInt64()
            : doc.Contains("created_at")
              && DateTime.TryParse(doc["created_at"].AsString, out var createdAt)
                ? new DateTimeOffset(createdAt).ToUnixTimeMilliseconds()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var defaultProfile = doc.Contains("defaultProfile") ? doc["defaultProfile"].AsString : "Default";
        var originalId = doc.Contains("_id") ? doc["_id"].AsObjectId.ToString() : null;

        // Build a domain Profile and decompose into V4 records
        var storeJson = doc.Contains("store") ? doc["store"].ToJson() : "{}";
        var store = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ProfileData>>(storeJson)
            ?? new Dictionary<string, ProfileData>();

        LoopProfileSettings? loopSettings = null;
        if (doc.Contains("loopSettings"))
        {
            loopSettings = System.Text.Json.JsonSerializer.Deserialize<LoopProfileSettings>(doc["loopSettings"].ToJson());
        }

        var profile = new Profile
        {
            Id = originalId ?? Guid.CreateVersion7().ToString(),
            DefaultProfile = defaultProfile,
            StartDate = doc.Contains("startDate") ? doc["startDate"].AsString : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mills = mills,
            CreatedAt = doc.Contains("created_at") ? doc["created_at"].AsString : null,
            Units = doc.Contains("units") ? doc["units"].AsString : "mg/dl",
            Store = store,
            EnteredBy = doc.Contains("enteredBy") ? doc["enteredBy"].AsString : null,
            LoopSettings = loopSettings,
        };

        using var scope = _serviceProvider.CreateScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<Nocturne.Core.Contracts.V4.IProfileDecomposer>();
        await decomposer.DecomposeAsync(profile, ct);
    }

    private async Task TransformFoodAsync(
        BsonDocument doc,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        var name = doc.Contains("name") ? doc["name"].AsString : "";
        var type = doc.Contains("type") ? doc["type"].AsString : "food";

        var originalId = doc.Contains("_id") ? doc["_id"].AsObjectId.ToString() : null;
        var exists = await dbContext.Foods.AnyAsync(
            f =>
                (originalId != null && f.OriginalId == originalId)
                || (f.Name == name && f.Type == type),
            ct
        );

        if (exists)
            return;

        var entity = new Infrastructure.Data.Entities.FoodEntity
        {
            Id = Guid.CreateVersion7(),
            OriginalId = originalId,
            Type = type,
            Category = doc.Contains("category") ? doc["category"].AsString : "",
            Subcategory = doc.Contains("subcategory") ? doc["subcategory"].AsString : "",
            Name = name,
            Portion = doc.Contains("portion") ? doc["portion"].ToDouble() : 0,
            Carbs = doc.Contains("carbs") ? doc["carbs"].ToDouble() : 0,
            Fat = doc.Contains("fat") ? doc["fat"].ToDouble() : 0,
            Protein = doc.Contains("protein") ? doc["protein"].ToDouble() : 0,
            Energy = doc.Contains("energy") ? doc["energy"].ToDouble() : 0,
            Gi = doc.Contains("gi") ? (Infrastructure.Data.Entities.GlycemicIndex)doc["gi"].ToInt32() : Infrastructure.Data.Entities.GlycemicIndex.Medium,
            Unit = doc.Contains("unit") ? doc["unit"].AsString : "g",
            Foods = doc.Contains("foods") ? doc["foods"].ToJson() : null,
            HideAfterUse = doc.Contains("hideAfterUse") && doc["hideAfterUse"].AsBoolean,
            Hidden = doc.Contains("hidden") && doc["hidden"].AsBoolean,
            Position = doc.Contains("position") ? doc["position"].ToInt32() : 99999,
        };

        dbContext.Foods.Add(entity);
    }

    /// <summary>
    /// Nightscout expects the api-secret header to be the SHA1 hash of the
    /// plaintext secret. If the value is already a 40-char hex string (i.e.
    /// already hashed), it is returned as-is.
    /// </summary>
    internal static string HashApiSecret(string apiSecret)
    {
        if (apiSecret.Length == 40 && apiSecret.All(char.IsAsciiHexDigit))
            return apiSecret.ToLowerInvariant();

        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(bytes);
    }

    private async Task MigrateSubjectsViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct)
    {
        _currentOperation = "Migrating subjects";
        var collectionName = "subjects";

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var totalSkipped = 0L;

        try
        {
            // 1. Fetch roles to build name->permissions lookup
            var rolePermissions = await FetchNightscoutRolePermissionsAsync(httpClient, ct);

            // 2. Fetch subjects
            var response = await httpClient.GetAsync("/api/v2/authorization/subjects", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to fetch subjects: {StatusCode}. The API secret may lack admin access. Skipping subject migration.",
                    response.StatusCode);
                UpdateCollectionProgress(collectionName, 0, 0, 0, true);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var subjects = System.Text.Json.JsonSerializer.Deserialize<NightscoutSubject[]>(
                content,
                s_caseInsensitiveJson) ?? [];

            UpdateCollectionProgress(collectionName, subjects.Length, 0, 0, false);
            UpdateOverallProgress();

            // 3. Pre-load existing token hashes for duplicate detection
            var existingHashes = await dbContext.Subjects
                .Where(s => s.AccessTokenHash != null)
                .Select(s => s.AccessTokenHash!)
                .ToHashSetAsync(ct);

            // 4. Pre-load existing Nocturne roles by name
            var nocturneRoles = await dbContext.Roles
                .ToDictionaryAsync(r => r.Name, r => r.Id, ct);

            foreach (var subject in subjects)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (string.IsNullOrWhiteSpace(subject.AccessToken))
                    {
                        totalSkipped++;
                        continue;
                    }

                    var tokenHash = HashAccessToken(subject.AccessToken);

                    if (existingHashes.Contains(tokenHash))
                    {
                        totalSkipped++;
                        continue;
                    }

                    // Determine if subject should be inactive ("denied" is only role)
                    var isDenied = subject.Roles is ["denied"];

                    var entity = new SubjectEntity
                    {
                        Id = Guid.CreateVersion7(),
                        Name = subject.Name ?? "Unnamed",
                        AccessTokenHash = tokenHash,
                        AccessTokenPrefix = $"{(subject.Name ?? "unknown").ToLowerInvariant()}-{subject.AccessToken[..Math.Min(8, subject.AccessToken.Length)]}",
                        IsActive = !isDenied,
                        Notes = "Migrated from Nightscout. Consider rotating to a Nocturne token.",
                        OriginalId = subject.MongoId ?? subject.Id,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        ApprovalStatus = "Approved",
                    };

                    dbContext.Subjects.Add(entity);
                    await dbContext.SaveChangesAsync(ct);

                    // Assign roles
                    foreach (var roleName in subject.Roles ?? [])
                    {
                        if (roleName == "denied")
                            continue;

                        if (!nocturneRoles.TryGetValue(roleName, out var roleId))
                        {
                            // Custom Nightscout role: create it with fetched permissions
                            var permissions = rolePermissions.GetValueOrDefault(roleName, []);
                            var roleEntity = new RoleEntity
                            {
                                Id = Guid.CreateVersion7(),
                                Name = roleName,
                                Description = "Migrated from Nightscout",
                                Permissions = permissions,
                                IsSystemRole = false,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                            };
                            dbContext.Roles.Add(roleEntity);
                            await dbContext.SaveChangesAsync(ct);
                            roleId = roleEntity.Id;
                            nocturneRoles[roleName] = roleId;
                        }

                        dbContext.SubjectRoles.Add(new SubjectRoleEntity
                        {
                            SubjectId = entity.Id,
                            RoleId = roleId,
                            AssignedAt = DateTime.UtcNow,
                        });
                    }

                    await dbContext.SaveChangesAsync(ct);

                    existingHashes.Add(tokenHash);
                    totalMigrated++;
                    UpdateCollectionProgress(collectionName, subjects.Length, totalMigrated, totalFailed, false);
                    UpdateOverallProgress();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to migrate subject {Name}", subject.Name);
                    totalFailed++;
                    dbContext.ChangeTracker.Clear();
                }
            }

            UpdateCollectionProgress(collectionName, subjects.Length, totalMigrated, totalFailed, true);
            UpdateOverallProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating subjects via API");
        }

        _logger.LogInformation(
            "Subject migration complete: {Migrated} migrated, {Skipped} skipped, {Failed} failed",
            totalMigrated, totalSkipped, totalFailed);
    }

    /// <summary>
    /// Fetches Nightscout role definitions and returns a name-to-permissions lookup.
    /// Falls back gracefully if the endpoint is inaccessible.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> FetchNightscoutRolePermissionsAsync(
        HttpClient httpClient, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var response = await httpClient.GetAsync("/api/v2/authorization/roles", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch Nightscout roles: {StatusCode}. Using default role mappings.", response.StatusCode);
                return result;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var roles = System.Text.Json.JsonSerializer.Deserialize<NightscoutRole[]>(
                content,
                s_caseInsensitiveJson) ?? [];

            foreach (var role in roles)
            {
                if (!string.IsNullOrWhiteSpace(role.Name))
                {
                    result[role.Name] = role.Permissions ?? [];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching Nightscout roles. Custom roles may not have correct permissions.");
        }

        return result;
    }

    private static string HashAccessToken(string accessToken)
    {
        var bytes = Encoding.UTF8.GetBytes(accessToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private record NightscoutSubject
    {
        public string? Id { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("_id")]
        public string? MongoId { get; init; }
        public string? Name { get; init; }
        public List<string> Roles { get; init; } = [];
        public string? AccessToken { get; init; }
    }

    private record NightscoutRole
    {
        public string? Name { get; init; }
        public List<string> Permissions { get; init; } = [];
    }
}
