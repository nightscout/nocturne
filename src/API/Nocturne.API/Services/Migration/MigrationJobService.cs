using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Constants;
using Nocturne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Service for managing migration jobs
/// </summary>
public interface IMigrationJobService
{
    Task<MigrationJobInfo> StartMigrationAsync(StartMigrationRequest request, CancellationToken ct = default);
    Task<MigrationJobStatus> GetStatusAsync(Guid jobId);
    Task CancelAsync(Guid jobId);
    Task<IReadOnlyList<MigrationJobInfo>> GetHistoryAsync();
    Task<TestMigrationConnectionResult> TestConnectionAsync(TestMigrationConnectionRequest request, CancellationToken ct = default);
    PendingMigrationConfig GetPendingConfig();
    Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementation of migration job service
/// </summary>
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
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public async Task<MigrationJobInfo> StartMigrationAsync(StartMigrationRequest request, CancellationToken ct = default)
    {
        var jobId = Guid.CreateVersion7();
        var sourceDesc = request.Mode == MigrationMode.Api
            ? request.NightscoutUrl
            : $"MongoDB: {request.MongoDatabaseName}";

        var jobInfo = new MigrationJobInfo
        {
            Id = jobId,
            Mode = request.Mode,
            CreatedAt = DateTime.UtcNow,
            SourceDescription = sourceDesc
        };

        var job = new MigrationJob(jobId, request, jobInfo, _logger, _serviceProvider);
        _jobs[jobId] = job;

        lock (_historyLock)
        {
            _history.Add(jobInfo);
        }

        // Start migration in background
        _ = Task.Run(async () =>
        {
            try
            {
                await job.ExecuteAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration job {JobId} failed", jobId);
            }
        }, ct);

        _logger.LogInformation("Started migration job {JobId} in {Mode} mode from {Source}",
            jobId, request.Mode, sourceDesc);

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
        CancellationToken ct = default)
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
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<TestMigrationConnectionResult> TestApiConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.NightscoutUrl))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = "Nightscout URL is required"
            };
        }

        using var scope = _serviceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(request.NightscoutUrl.TrimEnd('/'));

        // Add API secret header if provided
        if (!string.IsNullOrEmpty(request.NightscoutApiSecret))
        {
            httpClient.DefaultRequestHeaders.Add("api-secret", request.NightscoutApiSecret);
        }

        try
        {
            var response = await httpClient.GetAsync("/api/v1/status", ct);
            if (!response.IsSuccessStatusCode)
            {
                return new TestMigrationConnectionResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Failed to connect: {response.StatusCode}"
                };
            }

            return new TestMigrationConnectionResult
            {
                IsSuccess = true,
                SiteName = request.NightscoutUrl,
                AvailableCollections = ["entries", "treatments", "profile", "devicestatus"]
            };
        }
        catch (HttpRequestException ex)
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = $"Connection failed: {ex.Message}"
            };
        }
    }

    private async Task<TestMigrationConnectionResult> TestMongoConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.MongoConnectionString))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = "MongoDB connection string is required"
            };
        }

        if (string.IsNullOrEmpty(request.MongoDatabaseName))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = "MongoDB database name is required"
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
            entryCount = await entriesCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
        }

        if (collectionList.Contains("treatments"))
        {
            var treatmentsCollection = database.GetCollection<BsonDocument>("treatments");
            treatmentCount = await treatmentsCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
        }

        return new TestMigrationConnectionResult
        {
            IsSuccess = true,
            SiteName = request.MongoDatabaseName,
            EntryCount = entryCount,
            TreatmentCount = treatmentCount,
            AvailableCollections = collectionList
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
            HasMongoConnectionString = !string.IsNullOrEmpty(_configuration["MIGRATION_MONGO_CONNECTION_STRING"]),
            MongoDatabaseName = _configuration["MIGRATION_MONGO_DATABASE_NAME"]
        };
    }

    public async Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var sources = await dbContext.MigrationSources
            .OrderByDescending(s => s.LastMigrationAt ?? s.CreatedAt)
            .Select(s => new MigrationSourceDto
            {
                Id = s.Id,
                Mode = s.Mode == "MongoDb" ? MigrationMode.MongoDb : MigrationMode.Api,
                NightscoutUrl = s.NightscoutUrl,
                MongoDatabaseName = s.MongoDatabaseName,
                LastMigrationAt = s.LastMigrationAt,
                LastMigratedDataTimestamp = s.LastMigratedDataTimestamp,
                CreatedAt = s.CreatedAt
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

    public MigrationJob(
        Guid id,
        StartMigrationRequest request,
        MigrationJobInfo info,
        ILogger logger,
        IServiceProvider serviceProvider)
    {
        _id = id;
        _request = request;
        _info = info;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public MigrationJobStatus GetStatus() => new()
    {
        JobId = _id,
        State = _state,
        ProgressPercentage = _progressPercentage,
        CurrentOperation = _currentOperation,
        ErrorMessage = _errorMessage,
        StartedAt = _startedAt,
        CompletedAt = _completedAt,
        CollectionProgress = _collectionProgress.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
    };

    public void Cancel()
    {
        _cts.Cancel();
        _state = MigrationJobState.Cancelled;
    }

    public async Task ExecuteAsync(CancellationToken externalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCt);
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
            _errorMessage = ex.Message;
            _completedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Migration job {JobId} failed", _id);
        }
    }

    private async Task ExecuteApiMigrationAsync(CancellationToken ct)
    {
        _currentOperation = "Connecting to Nightscout";

        using var scope = _serviceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(_request.NightscoutUrl!.TrimEnd('/'));

        // Add API secret header if provided
        if (!string.IsNullOrEmpty(_request.NightscoutApiSecret))
        {
            httpClient.DefaultRequestHeaders.Add("api-secret", _request.NightscoutApiSecret);
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // Migrate entries
        if (_request.Collections.Count == 0 || _request.Collections.Contains("entries"))
        {
            await MigrateEntriesViaApiAsync(httpClient, dbContext, ct);
        }

        // Migrate treatments
        if (_request.Collections.Count == 0 || _request.Collections.Contains("treatments"))
        {
            await MigrateTreatmentsViaApiAsync(httpClient, dbContext, ct);
        }
    }

    private async Task MigrateEntriesViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct)
    {
        _currentOperation = "Migrating entries";
        var collectionName = "entries";

        _collectionProgress[collectionName] = new CollectionProgress
        {
            CollectionName = collectionName,
            TotalDocuments = 0,
            DocumentsMigrated = 0,
            DocumentsFailed = 0,
            IsComplete = false
        };

        var totalMigrated = 0L;
        var totalFailed = 0L;

        try
        {
            // Fetch entries from Nightscout API
            var response = await httpClient.GetAsync("/api/v1/entries.json?count=10000", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch entries: {StatusCode}", response.StatusCode);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var entries = System.Text.Json.JsonSerializer.Deserialize<Entry[]>(content) ?? [];

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var mills = entry.Mills;

                    // Check for duplicates
                    var exists = await dbContext.Entries
                        .AnyAsync(e => e.Mills == mills && e.Sgv == entry.Sgv, ct);

                    if (!exists)
                    {
                        dbContext.Entries.Add(new Infrastructure.Data.Entities.EntryEntity
                        {
                            Id = Guid.CreateVersion7(),
                            Type = entry.Type ?? "sgv",
                            Sgv = entry.Sgv,
                            Mgdl = entry.Mgdl,
                            Direction = entry.Direction,
                            Device = entry.Device,
                            Mills = mills,
                            DataSource = DataSources.MongoDbImport
                        });
                        totalMigrated++;
                    }
                }
                catch
                {
                    totalFailed++;
                }
            }

            await dbContext.SaveChangesAsync(ct);

            _collectionProgress[collectionName] = new CollectionProgress
            {
                CollectionName = collectionName,
                TotalDocuments = entries.Length,
                DocumentsMigrated = totalMigrated,
                DocumentsFailed = totalFailed,
                IsComplete = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating entries via API");
        }

        _logger.LogInformation("Migrated {Count} entries via API", totalMigrated);
    }

    private async Task MigrateTreatmentsViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct)
    {
        _currentOperation = "Migrating treatments";
        var collectionName = "treatments";

        _collectionProgress[collectionName] = new CollectionProgress
        {
            CollectionName = collectionName,
            TotalDocuments = 0,
            DocumentsMigrated = 0,
            DocumentsFailed = 0,
            IsComplete = false
        };

        var totalMigrated = 0L;
        var totalFailed = 0L;

        try
        {
            var response = await httpClient.GetAsync("/api/v1/treatments.json?count=10000", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch treatments: {StatusCode}", response.StatusCode);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var treatments = System.Text.Json.JsonSerializer.Deserialize<Treatment[]>(content) ?? [];

            foreach (var treatment in treatments)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var mills = treatment.CalculatedMills;

                    // Check for duplicates
                    var exists = await dbContext.Treatments
                        .AnyAsync(t => t.Mills == mills && t.EventType == treatment.EventType, ct);

                    if (!exists)
                    {
                        dbContext.Treatments.Add(new Infrastructure.Data.Entities.TreatmentEntity
                        {
                            Id = Guid.CreateVersion7(),
                            EventType = treatment.EventType,
                            Insulin = treatment.Insulin,
                            Carbs = treatment.Carbs,
                            Notes = treatment.Notes,
                            Duration = treatment.Duration,
                            Mills = mills,
                            DataSource = DataSources.MongoDbImport
                        });
                        totalMigrated++;
                    }
                }
                catch
                {
                    totalFailed++;
                }
            }

            await dbContext.SaveChangesAsync(ct);

            _collectionProgress[collectionName] = new CollectionProgress
            {
                CollectionName = collectionName,
                TotalDocuments = treatments.Length,
                DocumentsMigrated = totalMigrated,
                DocumentsFailed = totalFailed,
                IsComplete = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating treatments via API");
        }

        _logger.LogInformation("Migrated {Count} treatments via API", totalMigrated);
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
        var collectionsToMigrate = _request.Collections.Count > 0
            ? collectionList.Where(c => _request.Collections.Contains(c)).ToList()
            : collectionList.Where(c => c is "entries" or "treatments" or "devicestatus" or "profile").ToList();

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
        CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var totalDocs = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);

        _collectionProgress[collectionName] = new CollectionProgress
        {
            CollectionName = collectionName,
            TotalDocuments = totalDocs,
            DocumentsMigrated = 0,
            DocumentsFailed = 0,
            IsComplete = false
        };

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var batchSize = 1000;

        var findOptions = new FindOptions<BsonDocument> { BatchSize = batchSize };
        var cursor = await collection.FindAsync(FilterDefinition<BsonDocument>.Empty, findOptions, ct);

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
                    _logger.LogWarning(ex, "Failed to migrate document in {Collection}", collectionName);
                }
            }

            await dbContext.SaveChangesAsync(ct);

            _collectionProgress[collectionName] = new CollectionProgress
            {
                CollectionName = collectionName,
                TotalDocuments = totalDocs,
                DocumentsMigrated = totalMigrated,
                DocumentsFailed = totalFailed,
                IsComplete = false
            };
        }

        _collectionProgress[collectionName] = _collectionProgress[collectionName] with
        {
            IsComplete = true
        };

        _logger.LogInformation("Migrated {Count}/{Total} documents from {Collection}",
            totalMigrated, totalDocs, collectionName);
    }

    private async Task TransformAndSaveDocumentAsync(
        string collectionName,
        BsonDocument doc,
        NocturneDbContext dbContext,
        CancellationToken ct)
    {
        switch (collectionName)
        {
            case "entries":
                await TransformEntryAsync(doc, dbContext, ct);
                break;
            case "treatments":
                await TransformTreatmentAsync(doc, dbContext, ct);
                break;
            default:
                _logger.LogDebug("Skipping unsupported collection: {Collection}", collectionName);
                break;
        }
    }

    private async Task TransformEntryAsync(BsonDocument doc, NocturneDbContext dbContext, CancellationToken ct)
    {
        var mills = doc.Contains("date") ? doc["date"].ToInt64() :
            doc.Contains("mills") ? doc["mills"].ToInt64() :
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        double? sgv = doc.Contains("sgv") ? doc["sgv"].ToDouble() : null;

        // Check for duplicates
        var exists = await dbContext.Entries
            .AnyAsync(e => e.Mills == mills && e.Sgv == sgv, ct);

        if (exists) return;

        var entity = new Infrastructure.Data.Entities.EntryEntity
        {
            Id = Guid.CreateVersion7(),
            OriginalId = doc.Contains("_id") ? doc["_id"].ToString() : null,
            Type = doc.Contains("type") ? doc["type"].AsString : "sgv",
            Sgv = sgv,
            Mgdl = sgv ?? 0,
            Direction = doc.Contains("direction") ? doc["direction"].AsString : null,
            Device = doc.Contains("device") ? doc["device"].AsString : null,
            Mills = mills,
            DataSource = DataSources.MongoDbImport
        };

        dbContext.Entries.Add(entity);
    }

    private async Task TransformTreatmentAsync(BsonDocument doc, NocturneDbContext dbContext, CancellationToken ct)
    {
        var mills = doc.Contains("mills") ? doc["mills"].ToInt64() :
            doc.Contains("created_at") && DateTime.TryParse(doc["created_at"].AsString, out var createdAt)
                ? new DateTimeOffset(createdAt).ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var eventType = doc.Contains("eventType") ? doc["eventType"].AsString : "Note";

        // Check for duplicates
        var exists = await dbContext.Treatments
            .AnyAsync(t => t.Mills == mills && t.EventType == eventType, ct);

        if (exists) return;

        var entity = new Infrastructure.Data.Entities.TreatmentEntity
        {
            Id = Guid.CreateVersion7(),
            OriginalId = doc.Contains("_id") ? doc["_id"].ToString() : null,
            EventType = eventType,
            Insulin = doc.Contains("insulin") ? doc["insulin"].ToDouble() : null,
            Carbs = doc.Contains("carbs") ? doc["carbs"].ToDouble() : null,
            Notes = doc.Contains("notes") ? doc["notes"].AsString : null,
            Duration = doc.Contains("duration") ? doc["duration"].ToDouble() : null,
            Mills = mills,
            DataSource = DataSources.MongoDbImport
        };

        dbContext.Treatments.Add(entity);
    }
}
