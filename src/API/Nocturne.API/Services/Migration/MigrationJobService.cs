using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
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
        TenantContext? tenantContext,
        CancellationToken ct = default
    );

    /// <exception cref="KeyNotFoundException">Thrown when the migration job is not found for the given tenant.</exception>
    Task<MigrationJobStatus> GetStatusAsync(Guid tenantId, Guid jobId);

    /// <exception cref="KeyNotFoundException">Thrown when the migration job is not found for the given tenant.</exception>
    Task CancelAsync(Guid tenantId, Guid jobId);
    Task<IReadOnlyList<MigrationJobInfo>> GetHistoryAsync(Guid tenantId);
    Task<TestMigrationConnectionResult> TestConnectionAsync(
        TestMigrationConnectionRequest request,
        CancellationToken ct = default
    );
    PendingMigrationConfig GetPendingConfig();

    /// <summary>Lists the calling tenant's migration sources. Source URLs frequently identify a person, so sources are never listed cross-tenant.</summary>
    Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(Guid tenantId, CancellationToken ct = default);
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
    /// <summary>
    /// Registered by <c>AddMigrationServices</c> as a connector client — guarded and pinned. Asking
    /// the factory for the unnamed default instead gets neither.
    /// </summary>
    public const string HttpClientName = "NightscoutMigration";

    private readonly ILogger<MigrationJobService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<Guid, MigrationJob> _jobs = new();

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
        TenantContext? tenantContext,
        CancellationToken ct = default
    )
    {
        // Never start a migration without a resolved tenant. The job writes via the detached
        // background task, so an empty/unresolved tenant here would otherwise fall back to a
        // stale pooled DbContext tenant and import a third party's data into the wrong tenant.
        if (tenantContext is null || tenantContext.TenantId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A migration requires a resolved tenant context; refusing to start without one.");
        }

        var jobId = Guid.CreateVersion7();
        var tenantId = tenantContext.TenantId;
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

        var job = new MigrationJob(jobId, tenantId, request, jobInfo, tenantContext, _logger, _serviceProvider);

        // Record the job (and its source) before the work starts. The in-process task cannot
        // survive an API restart, but its record must — job history and "was this source ever
        // migrated?" checks read these rows, and without them a restart erases all evidence
        // that the run happened. Registered in the job map only after the record exists, so a
        // failed persist doesn't leave a phantom Pending job answering status probes.
        await job.PersistSnapshotAsync(ct);
        _jobs[jobId] = job;

        // Start migration on a detached background task. This deliberately does NOT use the
        // request's CancellationToken (HttpContext.RequestAborted): the migration is designed to
        // outlive the HTTP request that kicked it off, and tying it to the request token would
        // cancel/abort it as soon as that request completes. User-initiated cancellation flows
        // through the job's own CancellationTokenSource via Cancel().
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await job.ExecuteAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Migration job {JobId} failed", jobId);
                }
            },
            CancellationToken.None
        );

        _logger.LogInformation(
            "Started migration job {JobId} in {Mode} mode from {Source}",
            jobId,
            request.Mode,
            sourceDesc
        );

        return jobInfo;
    }

    public async Task<MigrationJobStatus> GetStatusAsync(Guid tenantId, Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job) && job.TenantId == tenantId)
        {
            return job.GetStatus();
        }

        // Not in memory (e.g. the API restarted since the job ran): serve the persisted record.
        var record = await FindRunAsync(tenantId, jobId)
            ?? throw new KeyNotFoundException($"Migration job {jobId} not found");

        return MigrationJob.StatusFromRecord(record);
    }

    public async Task CancelAsync(Guid tenantId, Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job) && job.TenantId == tenantId)
        {
            job.Cancel();
            await job.PersistSnapshotAsync(CancellationToken.None);
            _logger.LogInformation("Cancelled migration job {JobId}", jobId);
            return;
        }

        // A persisted record without a live job is terminal or orphaned by a restart — nothing
        // left to stop, but the id is valid, so don't report it as unknown.
        _ = await FindRunAsync(tenantId, jobId)
            ?? throw new KeyNotFoundException($"Migration job {jobId} not found");

        _logger.LogInformation("Cancel requested for migration job {JobId} which is no longer running", jobId);
    }

    public async Task<IReadOnlyList<MigrationJobInfo>> GetHistoryAsync(Guid tenantId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var runs = await db.MigrationRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return runs.Select(MigrationJob.InfoFromRecord).ToList();
    }

    private async Task<MigrationRunEntity?> FindRunAsync(Guid tenantId, Guid jobId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        return await db.MigrationRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == jobId && r.TenantId == tenantId);
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
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
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
        Guid tenantId,
        CancellationToken ct = default
    )
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var sources = await dbContext
            .MigrationSources
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.LastMigrationAt ?? s.CreatedAt)
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
    private readonly Guid _tenantId;
    private readonly StartMigrationRequest _request;
    private readonly MigrationJobInfo _info;
    private readonly TenantContext? _tenantContext;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The tenant that owns this migration job. Used to scope status/cancel lookups.</summary>
    public Guid TenantId => _tenantId;
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
        Guid tenantId,
        StartMigrationRequest request,
        MigrationJobInfo info,
        TenantContext? tenantContext,
        ILogger logger,
        IServiceProvider serviceProvider
    )
    {
        _id = id;
        _tenantId = tenantId;
        _request = request;
        _info = info;
        _tenantContext = tenantContext;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>Audit endpoint recorded for migration-owned writes.</summary>
    private const string AuditEndpoint = "service:migration";

    /// <summary>
    /// Creates a DI scope for migration work and propagates the owning tenant's context into it,
    /// so that tenant-scoped services (NocturneDbContext, decomposers, repositories) resolve and
    /// write under the correct tenant. The migration runs on a detached background task with no
    /// ambient request scope, so the tenant must be re-applied explicitly here — mirroring the
    /// pattern used by other background services (e.g. ConnectorBackgroundService).
    /// </summary>
    private IServiceScope CreateTenantScope()
    {
        var scope = _serviceProvider.CreateScope();
        if (_tenantContext is not null)
        {
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(_tenantContext);
            // Pin the RLS tenant on the pooled DbContext too: setting ITenantAccessor alone does
            // not retrofit an already-leased context (TenantConnectionInterceptor reads
            // NocturneDbContext.TenantId on connection open). Without this the detached migration
            // task could write under a stale pooled tenant. Mirrors ConnectorBackgroundService.
            scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = _tenantContext.TenantId;
        }

        // Attribute the imported records to the migration rather than to a human actor. A backfill
        // writes one row per historical treatment/entry, and without this every one of them also
        // appends a mutation_audit_log row. Mirrors ConnectorBackgroundService.
        return new SystemAttributedScope(
            scope,
            SystemAuditScope.PushForScope(scope.ServiceProvider, AuditEndpoint));
    }

    /// <summary>
    /// A DI scope whose ambient <see cref="IAuditContext"/> stays system-attributed for the
    /// scope's lifetime. Disposing releases the audit attribution, then the scope.
    /// </summary>
    private sealed class SystemAttributedScope(IServiceScope inner, IDisposable auditScope) : IServiceScope
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            auditScope.Dispose();
            inner.Dispose();
        }
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
        // Leave terminal states untouched — cancelling a job that already Completed/Failed must
        // not rewrite its (now persisted) outcome, which would also un-satisfy the startup
        // "was this source ever migrated?" check.
        if (_state is MigrationJobState.Pending or MigrationJobState.Validating or MigrationJobState.Running)
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
        await PersistSnapshotAsync(CancellationToken.None);

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
        finally
        {
            await PersistSnapshotAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Upserts the job's persisted run record (and its migration source) from the current
    /// in-memory state. The initial call (state Pending) propagates failures — a job whose
    /// record cannot be written must not start; later calls log instead, because the running
    /// work matters more than its record and the terminal snapshot retries.
    /// </summary>
    public async Task PersistSnapshotAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

            var sourceId = await UpsertSourceAsync(db, ct);

            var run = await db.MigrationRuns.FirstOrDefaultAsync(r => r.Id == _id, ct);
            if (run is null)
            {
                run = new MigrationRunEntity { Id = _id, CreatedAt = _info.CreatedAt };
                db.MigrationRuns.Add(run);
            }

            run.SourceId = sourceId;
            run.TenantId = _tenantId;
            run.Mode = _request.Mode.ToString();
            run.SourceDescription = _info.SourceDescription is { Length: > 512 } d ? d[..512] : _info.SourceDescription;
            run.State = _state.ToString();
            run.StartedAt = _startedAt == default ? _info.CreatedAt : _startedAt;
            run.CompletedAt = _completedAt;
            run.DateRangeStart = AsUtc(_request.StartDate);
            run.DateRangeEnd = AsUtc(_request.EndDate);
            run.ErrorMessage = _errorMessage;
            run.EntriesMigrated = (int)Math.Min(int.MaxValue, MigratedCount("entries"));
            run.TreatmentsMigrated = (int)Math.Min(int.MaxValue, MigratedCount("treatments"));

            if (_state == MigrationJobState.Completed)
            {
                var source = await db.MigrationSources.FirstAsync(s => s.Id == sourceId, ct);
                source.LastMigrationAt = _completedAt ?? DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (_state is not MigrationJobState.Pending)
        {
            _logger.LogError(ex, "Failed to persist migration job {JobId}", _id);
        }
    }

    private long MigratedCount(string collection) =>
        _collectionProgress.TryGetValue(collection, out var p) ? p.DocumentsMigrated : 0;

    /// <summary>
    /// Finds or creates the migration source row for this job's target. Sources dedupe per
    /// tenant on <see cref="MigrationSourceEntity.SourceIdentifier"/>: the Nightscout URL for
    /// API mode, a SHA-256 digest of the connection string for MongoDB mode. Only non-usable
    /// digests are stored — never the API secret or connection string themselves.
    /// </summary>
    private async Task<Guid> UpsertSourceAsync(NocturneDbContext db, CancellationToken ct)
    {
        var isApi = _request.Mode == MigrationMode.Api;
        var identifier = isApi
            ? ApiSourceIdentifier(_request.NightscoutUrl!)
            : MongoSourceIdentifier(_request.MongoConnectionString ?? string.Empty);

        var source = await db.MigrationSources.FirstOrDefaultAsync(
            s => s.TenantId == _tenantId && s.SourceIdentifier == identifier, ct);
        if (source is not null)
            return source.Id;

        source = new MigrationSourceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Mode = _request.Mode.ToString(),
            SourceIdentifier = identifier,
            NightscoutUrl = isApi ? _request.NightscoutUrl : null,
            NightscoutApiSecretHash = isApi && !string.IsNullOrEmpty(_request.NightscoutApiSecret)
                ? HashUtils.Sha256Hex(_request.NightscoutApiSecret)
                : null,
            MongoDatabaseName = isApi ? null : _request.MongoDatabaseName,
            CreatedAt = DateTime.UtcNow,
        };
        db.MigrationSources.Add(source);
        return source.Id;
    }

    /// <summary>Canonical source identifier for an API-mode migration. Shared with the startup pending-migration check so the two always agree.</summary>
    internal static string ApiSourceIdentifier(string nightscoutUrl) => nightscoutUrl.TrimEnd('/');

    /// <summary>Canonical source identifier for a MongoDB-mode migration: a non-usable digest, never the connection string itself.</summary>
    internal static string MongoSourceIdentifier(string connectionString) =>
        HashUtils.Sha256Hex(connectionString);

    /// <summary>Npgsql rejects Local/Unspecified kinds for timestamptz; normalize optional caller-supplied dates.</summary>
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };

    /// <summary>Reconstructs a status snapshot from a persisted run (post-restart lookups).</summary>
    public static MigrationJobStatus StatusFromRecord(MigrationRunEntity run)
    {
        var state = Enum.TryParse<MigrationJobState>(run.State, out var s)
            ? s
            : MigrationJobState.Interrupted;

        return new MigrationJobStatus
        {
            JobId = run.Id,
            State = state,
            ProgressPercentage = state == MigrationJobState.Completed ? 100 : 0,
            ErrorMessage = run.ErrorMessage,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            CollectionProgress = new Dictionary<string, CollectionProgress>
            {
                ["entries"] = new()
                {
                    CollectionName = "entries",
                    DocumentsMigrated = run.EntriesMigrated,
                    IsComplete = state == MigrationJobState.Completed,
                },
                ["treatments"] = new()
                {
                    CollectionName = "treatments",
                    DocumentsMigrated = run.TreatmentsMigrated,
                    IsComplete = state == MigrationJobState.Completed,
                },
            },
        };
    }

    /// <summary>Reconstructs a history entry from a persisted run.</summary>
    public static MigrationJobInfo InfoFromRecord(MigrationRunEntity run) => new()
    {
        Id = run.Id,
        Mode = Enum.TryParse<MigrationMode>(run.Mode, out var m) ? m : MigrationMode.Api,
        CreatedAt = run.CreatedAt,
        SourceDescription = run.SourceDescription,
        State = Enum.TryParse<MigrationJobState>(run.State, out var s) ? s : MigrationJobState.Interrupted,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        ErrorMessage = run.ErrorMessage,
    };

    private long _totalDocumentsAllCollections;
    private long _migratedDocumentsAllCollections; // computed by UpdateOverallProgress

    private async Task ExecuteApiMigrationAsync(CancellationToken ct)
    {
        _currentOperation = "Connecting to Nightscout";

        using var scope = CreateTenantScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(MigrationJobService.HttpClientName);
        httpClient.BaseAddress = new Uri(_request.NightscoutUrl!.TrimEnd('/'));

        // Add API secret header if provided (Nightscout expects the SHA1 hash)
        if (!string.IsNullOrEmpty(_request.NightscoutApiSecret))
        {
            httpClient.DefaultRequestHeaders.Add("api-secret", HashApiSecret(_request.NightscoutApiSecret));
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // Build the list of collections to migrate
        var allCollections = new (string name, Func<HttpClient, CancellationToken, Task> migrate)[]
        {
            ("subjects", (client, token) => MigrateSubjectsViaApiAsync(client, dbContext, token)),
            ("entries", (client, token) => MigratePagedCollectionAsync(client, s_entriesCollection, token)),
            ("treatments", (client, token) => MigratePagedCollectionAsync(client, s_treatmentsCollection, token)),
            ("devicestatus", (client, token) => MigratePagedCollectionAsync(client, s_deviceStatusCollection, token)),
            ("profile", MigrateProfilesViaApiAsync),
            ("food", (client, token) => MigrateFoodViaApiAsync(client, dbContext, token)),
            ("activity", (client, token) => MigratePagedCollectionAsync(client, s_activityCollection, token)),
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

        foreach (var (_, migrate) in collectionsToMigrate)
        {
            await migrate(httpClient, ct);
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

    /// <summary>
    ///     Anchor for the first page of a collection fetch. Nightscout applies an implicit
    ///     recency window (roughly the last four days) to any query carrying no date filter,
    ///     which truncates an unbounded first page — the short page then ends pagination,
    ///     silently importing days of history instead of years. Anchoring the upper bound
    ///     keeps every request explicitly dated.
    /// </summary>
    private static DateTime FirstPageAnchor => DateTime.UtcNow;

    /// <summary>
    ///     Page size for every legacy-API pull. The merged v1 reads (devicestatus, activity) clamp
    ///     to <see cref="LegacyReadLimits.MaxMergedCount"/>, and the loops terminate on a short
    ///     page, so a larger value here would silently end those pulls after one page.
    /// </summary>
    private const int ApiPageSize = LegacyReadLimits.MaxMergedCount;

    /// <summary>
    ///     How a paged pull bounds and advances its time cursor: <paramref name="Filter"/> is the
    ///     query-string fragment restricting a page to records at or before the cursor, and
    ///     <paramref name="Oldest"/> reads the page's oldest record, answering <c>null</c> when the
    ///     page carries no usable timestamp to page back from.
    /// </summary>
    private sealed record PageCursor(
        Func<DateTime, string> Filter,
        Func<IReadOnlyList<ProcessableDocumentBase>, DateTime?> Oldest
    );

    /// <summary>Entries page on the numeric <c>date</c> field, which mirrors mills exactly.</summary>
    private static readonly PageCursor s_dateCursor = new(
        to => $"&find[date][$lte]={new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeMilliseconds()}",
        page =>
        {
            var oldestMs = page.Min(d => d.Mills);
            return oldestMs <= 0
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(oldestMs).UtcDateTime;
        });

    /// <summary>Every other collection pages on the ISO-8601 <c>created_at</c> string.</summary>
    private static readonly PageCursor s_createdAtCursor = new(
        to => $"&find[created_at][$lte]={to.ToUniversalTime():o}",
        page => page
            .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
            .Where(dt => dt.HasValue)
            .Min());

    /// <summary>
    ///     A legacy collection pulled page by page over a time cursor. <paramref name="Name"/> is
    ///     both the v1 route segment and the progress key; <paramref name="Label"/> names the
    ///     records in operation and log text; <paramref name="Decompose"/> resolves the
    ///     collection's decomposer from the migration's tenant scope once per pull.
    /// </summary>
    private sealed record PagedCollection<T>(
        string Name,
        string Label,
        PageCursor Cursor,
        Func<IServiceProvider, Func<T[], CancellationToken, Task>> Decompose
    ) where T : ProcessableDocumentBase;

    private static readonly PagedCollection<Entry> s_entriesCollection = new(
        "entries", "entries", s_dateCursor,
        sp =>
        {
            var decomposer = sp.GetRequiredService<IEntryDecomposer>();
            return (page, ct) => decomposer.DecomposeBatchAsync(page, WriteOrigin.Backfill, ct);
        });

    private static readonly PagedCollection<Treatment> s_treatmentsCollection = new(
        "treatments", "treatments", s_createdAtCursor,
        sp =>
        {
            var decomposer = sp.GetRequiredService<ITreatmentDecomposer>();
            return (page, ct) => decomposer.DecomposeBatchAsync(page, WriteOrigin.Backfill, ct);
        });

    private static readonly PagedCollection<DeviceStatus> s_deviceStatusCollection = new(
        "devicestatus", "device statuses", s_createdAtCursor,
        sp =>
        {
            var decomposer = sp.GetRequiredService<IDeviceStatusDecomposer>();
            return (page, ct) => decomposer.DecomposeBatchAsync(page, source: null, WriteOrigin.Backfill, ct);
        });

    private static readonly PagedCollection<Activity> s_activityCollection = new(
        "activity", "activities", s_createdAtCursor,
        sp =>
        {
            var decomposer = sp.GetRequiredService<IActivityDecomposer>();
            return (page, ct) => decomposer.DecomposeBatchAsync(page, WriteOrigin.Backfill, ct);
        });

    private async Task MigratePagedCollectionAsync<T>(
        HttpClient httpClient,
        PagedCollection<T> collection,
        CancellationToken ct
    ) where T : ProcessableDocumentBase
    {
        _currentOperation = $"Migrating {collection.Label}";
        var knownTotal = _collectionProgress.TryGetValue(collection.Name, out var existing)
            ? existing.TotalDocuments : 0;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        DateTime? currentTo = FirstPageAnchor;

        using var scope = CreateTenantScope();
        var decompose = collection.Decompose(scope.ServiceProvider);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"/api/v1/{collection.Name}.json?count={ApiPageSize}";
            if (currentTo.HasValue)
                url += collection.Cursor.Filter(currentTo.Value);

            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Failed to fetch {Collection}: {StatusCode}", collection.Label, response.StatusCode);
                break;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var page = System.Text.Json.JsonSerializer.Deserialize<T[]>(content) ?? [];

            if (page.Length == 0) break;

            try
            {
                await decompose(page, ct);
                totalMigrated += page.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompose {Collection} page", collection.Label);
                totalFailed += page.Length;
            }

            UpdateCollectionProgress(collection.Name,
                Math.Max(knownTotal, totalMigrated + totalFailed),
                totalMigrated, totalFailed, false);
            UpdateOverallProgress();

            if (page.Length < ApiPageSize) break;

            var oldestDate = collection.Cursor.Oldest(page);

            if (!oldestDate.HasValue) break;
            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value) break;
            currentTo = oldestDate.Value.AddMilliseconds(-1);
        }

        UpdateCollectionProgress(collection.Name, Math.Max(knownTotal, totalMigrated + totalFailed),
            totalMigrated, totalFailed, true);
        UpdateOverallProgress();
        _logger.LogInformation(
            "Migrated {Count} {Collection} via API", totalMigrated, collection.Label);
    }

    private async Task MigrateProfilesViaApiAsync(
        HttpClient httpClient,
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

            using var scope = CreateTenantScope();
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

                    await decomposer.DecomposeAsync(profile, WriteOrigin.Backfill, ct);
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

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var url = $"/api/v1/food.json?count={ApiPageSize}&skip={totalSkipped}";

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

                if (foods.Length < ApiPageSize) break;
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

    private async Task ExecuteMongoMigrationAsync(CancellationToken ct)
    {
        _currentOperation = "Connecting to MongoDB";

        var client = new MongoClient(_request.MongoConnectionString);
        var database = client.GetDatabase(_request.MongoDatabaseName);

        using var scope = CreateTenantScope();
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

        using var scope = CreateTenantScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<Core.Contracts.V4.IDeviceStatusDecomposer>();
        await decomposer.DecomposeAsync(status, source: null, WriteOrigin.Backfill, ct);
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

        using var scope = CreateTenantScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<Nocturne.Core.Contracts.V4.IProfileDecomposer>();
        await decomposer.DecomposeAsync(profile, WriteOrigin.Backfill, ct);
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
                .ToDictionaryAsync(r => r.Name, r => r, ct);

            // Nightscout derives each subject's token from digest = sha1(sha1(api_secret) + _id).
            // HashApiSecret yields exactly that inner sha1(api_secret), so with the mongo _id we
            // can reconstruct the full 40-char digest and store it for 1:1 legacy token matching.
            var hashedSecret = string.IsNullOrEmpty(_request.NightscoutApiSecret)
                ? null
                : HashApiSecret(_request.NightscoutApiSecret);

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

                    var tokenHash = HashUtils.Sha256Hex(subject.AccessToken);

                    if (existingHashes.Contains(tokenHash))
                    {
                        totalSkipped++;
                        continue;
                    }

                    var roles = await ResolveRolesAsync(dbContext, nocturneRoles, rolePermissions, subject.Roles, ct);

                    // Determine if subject should be inactive ("denied" is only role)
                    var isDenied = subject.Roles is ["denied"];

                    var mongoId = subject.MongoId ?? subject.Id;
                    var legacyDigest = Auth.LegacyNightscoutToken.DeriveDigest(hashedSecret, mongoId, subject.AccessToken);

                    var entity = new SubjectEntity
                    {
                        Id = Guid.CreateVersion7(),
                        Name = subject.Name ?? "Unnamed",
                        AccessTokenHash = tokenHash,
                        AccessTokenPrefix = $"{(subject.Name ?? "unknown").ToLowerInvariant()}-{subject.AccessToken[..Math.Min(8, subject.AccessToken.Length)]}",
                        LegacyTokenDigest = legacyDigest,
                        IsActive = !isDenied,
                        Notes = "Migrated from Nightscout. Consider rotating to a Nocturne token.",
                        OriginalId = mongoId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        ApprovalStatus = "Approved",
                    };

                    dbContext.Subjects.Add(entity);
                    await dbContext.SaveChangesAsync(ct);

                    foreach (var role in roles)
                    {
                        dbContext.SubjectRoles.Add(new SubjectRoleEntity
                        {
                            SubjectId = entity.Id,
                            RoleId = role.Id,
                            AssignedAt = DateTime.UtcNow,
                        });
                    }

                    AddTenantMembership(dbContext, entity.Id, GrantedPermissions(roles, rolePermissions));
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
    /// Resolves a Nightscout subject's role names to Nocturne roles, creating any the instance does
    /// not already have from the permissions fetched off the source. The <paramref name="knownRoles"/>
    /// lookup is updated so each custom role is created once per run.
    /// </summary>
    /// <remarks>
    /// "denied" is dropped: it is Nightscout's way of spelling "no access", carried instead by
    /// <see cref="SubjectEntity.IsActive"/>, and a role row for it would grant nothing anyway.
    /// </remarks>
    private static async Task<List<RoleEntity>> ResolveRolesAsync(
        NocturneDbContext dbContext,
        Dictionary<string, RoleEntity> knownRoles,
        Dictionary<string, List<string>> sourcePermissions,
        List<string>? roleNames,
        CancellationToken ct)
    {
        var resolved = new List<RoleEntity>();

        foreach (var roleName in roleNames ?? [])
        {
            if (roleName == "denied")
                continue;

            if (!knownRoles.TryGetValue(roleName, out var role))
            {
                role = new RoleEntity
                {
                    Id = Guid.CreateVersion7(),
                    Name = roleName,
                    Description = "Migrated from Nightscout",
                    Permissions = sourcePermissions.GetValueOrDefault(roleName, []),
                    IsSystemRole = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                dbContext.Roles.Add(role);
                await dbContext.SaveChangesAsync(ct);
                knownRoles[roleName] = role;
            }

            resolved.Add(role);
        }

        return resolved;
    }

    /// <summary>
    /// The legacy permissions a subject's roles grant it on the source instance. The source's own
    /// definition wins over the same-named Nocturne role, which may be a wider seeded role that
    /// merely shares a name (a hand-written Nightscout role called <c>admin</c> need not mean
    /// <c>*</c>). The local definition is the fallback for a source whose roles endpoint was
    /// inaccessible.
    /// </summary>
    private static IEnumerable<string> GrantedPermissions(
        List<RoleEntity> roles, Dictionary<string, List<string>> sourcePermissions) =>
        roles.SelectMany(role => sourcePermissions.TryGetValue(role.Name, out var fromSource)
            ? fromSource
            : role.Permissions);

    /// <summary>
    /// Makes an imported subject a member of the tenant being migrated into. Without the membership
    /// the subject authenticates and is then dropped straight back to unauthenticated:
    /// <c>AuthenticationMiddleware</c> requires a membership row for every credential type it does
    /// not exempt, and a legacy access token is not exempt.
    /// </summary>
    /// <remarks>
    /// The imported permissions are carried directly rather than mapped onto the seed tenant roles,
    /// which do not line up with Nightscout's — Viewer is narrower than <c>readable</c>, Caretaker
    /// wider than <c>careportal</c> — and which have no answer at all for a custom Nightscout role.
    /// <see cref="ScopeTranslator"/> drops anything it cannot translate, so a permission with no
    /// Nocturne equivalent grants nothing, and a subject left with nothing gets no membership at
    /// all rather than an entry on the member list that cannot do anything.
    /// </remarks>
    private void AddTenantMembership(
        NocturneDbContext dbContext, Guid subjectId, IEnumerable<string> legacyPermissions)
    {
        var scopes = ScopeTranslator.FromPermissions(legacyPermissions);

        if (scopes.Count == 0)
            return;

        // A "*" grant is stored as the single superuser atom: NormalizeMemberPermissions expands it
        // back to every scope, so spelling out the expansion would only bake today's scope list in.
        List<string> permissions = scopes.Contains(Scope.FullAccess)
            ? [Scope.FullAccess]
            : [.. scopes];

        dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
            DirectPermissions = permissions,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
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
