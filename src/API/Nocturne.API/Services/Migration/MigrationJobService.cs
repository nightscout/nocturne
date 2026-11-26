using System.Globalization;
using Nocturne.API.Services.Health;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Nocturne.API.Helpers;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using NightscoutAddress = Nocturne.Connectors.Nightscout.Services.NightscoutAddress;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Auth;
using AuthRole = Nocturne.Core.Models.Authorization.Role;
using Nocturne.Core.Models.Authorization;
using DecompositionResult = Nocturne.Core.Models.V4.DecompositionResult;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Service for managing MongoDB-to-Nocturne migration jobs. Supports starting, monitoring,
/// and cancelling migrations, as well as testing source connections and retrieving migration history.
/// </summary>
public interface IMigrationJobService
{
    /// <param name="request">What to import.</param>
    /// <param name="tenantContext">The tenant the job imports into.</param>
    /// <param name="starter">
    /// Who starts it (<see cref="Extensions.HttpContextExtensions.CredentialMakerAsync"/>), the maker of
    /// every token it adopts; null when no subject starts it, and the tokens then have no maker.
    /// </param>
    /// <param name="ct">Cancels the start.</param>
    /// <exception cref="MigrationAlreadyRunningException">Thrown when the tenant already has a job in flight.</exception>
    Task<MigrationJobInfo> StartMigrationAsync(
        StartMigrationRequest request,
        TenantContext? tenantContext,
        CredentialMaker? starter,
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
    /// <summary>
    /// The instance's configured migration source, with its URL in its
    /// <see cref="NightscoutAddress.Shown"/> form, to a caller with
    /// <paramref name="instanceStanding"/>; no pending source to any other.
    /// </summary>
    /// <param name="instanceStanding">Whether the caller holds <see cref="Auth.InstanceStanding.HeldByAsync"/>.</param>
    PendingMigrationConfig GetPendingConfig(bool instanceStanding);

    /// <summary>
    /// The Nightscout URL an API-mode request reads: <paramref name="requested"/>, or the configured
    /// source's when it is blank and the caller has <paramref name="instanceStanding"/>.
    /// <see cref="GetPendingConfig"/> gives a form only the shown form of the configured URL, so a
    /// form that keeps it sends none.
    /// </summary>
    /// <param name="requested">The URL the request names.</param>
    /// <param name="instanceStanding">Whether the caller holds <see cref="Auth.InstanceStanding.HeldByAsync"/>.</param>
    string? ResolveNightscoutUrl(string? requested, bool instanceStanding);

    /// <summary>Lists the calling tenant's migration sources. Source URLs frequently identify a person, so sources are never listed cross-tenant.</summary>
    Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Runs the tenant's live job for one slice, from where its last slice stopped. For
    /// <see cref="MigrationRunJob"/>.
    /// </summary>
    /// <returns>Whether the job has more to do; false when it ended or the tenant has none live.</returns>
    Task<bool> RunSliceAsync(TenantContext tenant, CancellationToken ct);

    /// <summary>Reads a Nightscout instance's display units and current profile's units and timezone.</summary>
    /// <exception cref="MigrationSourceException">The instance could not be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">The instance answered with something other than JSON.</exception>
    Task<NightscoutDisplaySettings> ReadDisplaySettingsAsync(
        string nightscoutUrl, string? apiSecret, CancellationToken ct = default);
}

/// <summary>
/// Implements <see cref="IMigrationJobService"/>. A job is its <c>migration_runs</c> row: starting
/// one writes the row and requests <see cref="MigrationRunJob"/>, which runs it a slice at a time,
/// so any replica answers its status and takes its cancel, and a job whose process stopped goes on
/// from its last checkpoint on whichever replica claims it next.
/// </summary>
/// <remarks>
/// A start, a cancel and each checkpoint take the tenant's run lock (<see cref="RunLockClass"/>), so
/// a tenant has one live job and none of them overwrites what another has just written.
/// </remarks>
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
    private readonly INocturneDbContextFactory _contexts;
    private readonly ISecretEncryptionService _secrets;
    private readonly TimeProvider _timeProvider;

    /// <summary>The jobs this process is running a slice of, so a cancel stops one at once.</summary>
    private readonly ConcurrentDictionary<Guid, MigrationJob> _running = new();

    /// <summary>The job-ledger job that runs a tenant's live migration.</summary>
    internal const string RunJobName = "migration-run";

    /// <summary>The advisory lock class of a tenant's run lock.</summary>
    internal const int RunLockClass = 0x4E4D_4952;

    private static readonly string[] s_liveStates = [.. MigrationJobStates.LiveNames];

    private const string UnreadableRequestMessage =
        "Nocturne could not read the source details saved for this import, so it could not continue. Start the import again.";

    /// <summary>How long one slice of a job lasts before it hands back to the scheduler.</summary>
    internal TimeSpan SliceLength { get; init; } = TimeSpan.FromMinutes(5);

    public MigrationJobService(
        ILogger<MigrationJobService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        INocturneDbContextFactory contexts,
        ISecretEncryptionService secrets,
        TimeProvider timeProvider
    )
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _contexts = contexts;
        _secrets = secrets;
        _timeProvider = timeProvider;
    }

    private static IQueryable<MigrationRunEntity> LiveRuns(NocturneDbContext db, Guid tenantId) =>
        db.MigrationRuns.Where(r => r.TenantId == tenantId && s_liveStates.Contains(r.State));

    public async Task<MigrationJobInfo> StartMigrationAsync(
        StartMigrationRequest request,
        TenantContext? tenantContext,
        CredentialMaker? starter,
        CancellationToken ct = default
    )
    {
        // Never start a migration without a resolved tenant: the job writes under whatever tenant
        // its row names.
        if (tenantContext is null || tenantContext.TenantId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A migration requires a resolved tenant context; refusing to start without one.");
        }

        var jobId = Guid.CreateVersion7();
        var tenantId = tenantContext.TenantId;
        var sourceDesc =
            request.Mode == MigrationMode.Api
                ? request.NightscoutUrl is { } url ? NightscoutAddress.Shown(url) : null
                : $"MongoDB: {request.MongoDatabaseName}";

        var jobInfo = new MigrationJobInfo
        {
            Id = jobId,
            Mode = request.Mode,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            SourceDescription = sourceDesc,
        };

        var job = new MigrationJob(jobId, tenantId, request, jobInfo, tenantContext, _logger, _serviceProvider, _timeProvider)
        {
            StoredRequest = _secrets.Encrypt(System.Text.Json.JsonSerializer.Serialize(request)),
            Starter = starter,
        };

        // One migration per tenant at a time: two runs over the same target race on their inserts.
        // The row and the request for its job commit together, so no row is left waiting for a job
        // nothing asked for.
        await using var db = await _contexts.CreateForTenantAsync(tenantId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(RunLockClass, tenantId, token);
            if (await LiveRuns(db, tenantId).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(token) is { } live)
                throw new MigrationAlreadyRunningException(live);

            await job.WriteAsync(db, token);
            await db.SaveChangesAsync(token);
            await JobLedgerRepository.RequestInTransactionAsync(db, RunJobName, token);
            return jobId;
        }, verifySucceeded: (id, token) => db.MigrationRuns.AnyAsync(r => r.Id == id, token), ct: ct);

        _logger.LogInformation(
            "Started migration job {JobId} in {Mode} mode from {Source}",
            jobId,
            request.Mode,
            LogText.Sanitize(sourceDesc)
        );

        return jobInfo;
    }

    /// <inheritdoc />
    public async Task<bool> RunSliceAsync(TenantContext tenant, CancellationToken ct)
    {
        MigrationRunEntity? run;
        await using (var db = await _contexts.CreateAsync(ct))
        {
            run = await LiveRuns(db, tenant.TenantId).AsNoTracking().OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        }

        if (run is null)
            return false;

        if (ReadRequest(run) is not { } request)
        {
            _logger.LogError("Migration job {JobId} has no readable request and cannot continue", run.Id);
            await EndAsync(tenant.TenantId, run.Id, MigrationJobState.Failed, UnreadableRequestMessage);
            return false;
        }

        var job = MigrationJob.Resume(run, request, tenant, _logger, _serviceProvider, _timeProvider);
        _running[run.Id] = job;
        try
        {
            await job.ExecuteAsync(SliceLength, ct);
        }
        finally
        {
            _running.TryRemove(KeyValuePair.Create(run.Id, job));
        }

        // The record says whether the job has ended, not the slice: an end the slice could not
        // store leaves the record live, and the next slice reaches that end again and stores it.
        await using var after = await _contexts.CreateAsync(ct);
        return await LiveRuns(after, tenant.TenantId).AnyAsync(r => r.Id == run.Id, ct);
    }

    private StartMigrationRequest? ReadRequest(MigrationRunEntity run)
    {
        if (string.IsNullOrEmpty(run.RequestEncrypted) || !_secrets.CanDecrypt(run.RequestEncrypted))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<StartMigrationRequest>(_secrets.Decrypt(run.RequestEncrypted));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Ends the tenant's job in <paramref name="state"/> if it is still live.</summary>
    /// <returns>Whether it was live.</returns>
    /// <exception cref="KeyNotFoundException">The tenant has no such job.</exception>
    private async Task<bool> EndAsync(Guid tenantId, Guid jobId, MigrationJobState state, string? message)
    {
        await using var db = await _contexts.CreateForTenantAsync(tenantId);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(RunLockClass, tenantId, token);
            var run = await db.MigrationRuns.FirstOrDefaultAsync(r => r.Id == jobId && r.TenantId == tenantId, token)
                ?? throw new KeyNotFoundException($"Migration job {jobId} not found");
            if (!s_liveStates.Contains(run.State))
                return false;

            run.State = state.ToString();
            run.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            run.ErrorMessage = message ?? run.ErrorMessage;
            run.Checkpoint = null;
            run.RequestEncrypted = null;
            run.Revision++;
            await db.SaveChangesAsync(token);
            return true;
        });
    }

    public async Task<MigrationJobStatus> GetStatusAsync(Guid tenantId, Guid jobId)
    {
        var record = await FindRunAsync(tenantId, jobId)
            ?? throw new KeyNotFoundException($"Migration job {jobId} not found");

        return MigrationJob.StatusFromRecord(record, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <remarks>
    /// A job running in this process stops at once. One running in another stops before its next
    /// page, or its next batch of a MongoDB collection, when it finds its row ended.
    /// </remarks>
    public async Task CancelAsync(Guid tenantId, Guid jobId)
    {
        var wasLive = await EndAsync(tenantId, jobId, MigrationJobState.Cancelled, message: null);
        if (_running.TryGetValue(jobId, out var job))
            job.Cancel();

        if (wasLive)
            _logger.LogInformation("Cancelled migration job {JobId}", jobId);
        else
            _logger.LogInformation("Cancel requested for migration job {JobId} which is no longer running", jobId);
    }

    public async Task<IReadOnlyList<MigrationJobInfo>> GetHistoryAsync(Guid tenantId)
    {
        await using var db = await _contexts.CreateAsync();

        var runs = await db.MigrationRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return runs.Select(MigrationJob.InfoFromRecord).ToList();
    }

    private async Task<MigrationRunEntity?> FindRunAsync(Guid tenantId, Guid jobId)
    {
        await using var db = await _contexts.CreateAsync();
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

        if (!NightscoutAddress.TryResolve(request.NightscoutUrl, out var address))
        {
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = NightscoutAddress.InvalidUrlMessage,
            };
        }

        var httpClient = CreateSourceClient(address, request.NightscoutApiSecret);

        try
        {
            await MigrationJob.ReadFromSourceAsync(
                httpClient, "api/v1/status", "status", ct, NightscoutRead.ImportProbe);

            return new TestMigrationConnectionResult
            {
                IsSuccess = true,
                SiteName = NightscoutAddress.Shown(request.NightscoutUrl),
                AvailableCollections = ["subjects", "entries", "treatments", "profile", "devicestatus", "food", "activity"],
            };
        }
        catch (MigrationSourceException ex)
        {
            // The button and the run reach the same source the same way, so a test that passes and
            // a run that then fails on the connection would be a contradiction the user has to
            // resolve; both report the cause in the same words.
            return new TestMigrationConnectionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    public async Task<NightscoutDisplaySettings> ReadDisplaySettingsAsync(
        string nightscoutUrl, string? apiSecret, CancellationToken ct = default)
    {
        if (!NightscoutAddress.TryResolve(nightscoutUrl, out var address))
            throw new MigrationSourceException(NightscoutAddress.InvalidUrlMessage, MigrationFailureCause.Unreachable);

        var httpClient = CreateSourceClient(address, apiSecret);
        var status = await MigrationJob.ReadFromSourceAsync(
            httpClient, "api/v1/status.json", "status", ct, NightscoutRead.ImportProbe);
        var profiles = await MigrationJob.ReadFromSourceAsync(httpClient, "api/v1/profile.json", "profile", ct);
        return NightscoutDisplaySettings.Parse(status, profiles);
    }

    private HttpClient CreateSourceClient(NightscoutAddress address, string? apiSecret)
    {
        var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        httpClient.BaseAddress = address.BaseUri;

        // Nightscout expects the SHA1 hash of the secret.
        if (!string.IsNullOrEmpty(apiSecret))
            httpClient.DefaultRequestHeaders.Add("api-secret", MigrationJob.HashApiSecret(apiSecret));

        return httpClient;
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

    /// <inheritdoc />
    public PendingMigrationConfig GetPendingConfig(bool instanceStanding) =>
        instanceStanding && ConfiguredMigrationSource.Read(_configuration) is { } source
            ? new PendingMigrationConfig
            {
                HasPendingConfig = true,
                Mode = source.Mode,
                NightscoutUrl = source.NightscoutUrl is { } url ? NightscoutAddress.Shown(url) : null,
                HasApiSecret = source.HasApiSecret,
                HasMongoConnectionString = source.MongoConnectionString is not null,
                MongoDatabaseName = source.MongoDatabaseName,
            }
            : new PendingMigrationConfig { HasPendingConfig = false };

    /// <inheritdoc />
    public string? ResolveNightscoutUrl(string? requested, bool instanceStanding) =>
        string.IsNullOrWhiteSpace(requested)
        && instanceStanding
        && ConfiguredMigrationSource.Read(_configuration)?.NightscoutUrl is { } configured
            ? configured
            : requested;

    public async Task<IReadOnlyList<MigrationSourceDto>> GetSourcesAsync(
        Guid tenantId,
        CancellationToken ct = default
    )
    {
        await using var dbContext = await _contexts.CreateAsync(ct);

        return await dbContext
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
    }
}

/// <summary>
/// One migration job, run a slice at a time. What a slice needs from the ones before it is in
/// <see cref="Checkpoint"/>, which <see cref="PauseAsync"/> stores on the job's run record before each
/// page, and <see cref="Resume"/> builds the job for the next slice from that record.
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
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The request as <see cref="MigrationRunEntity.RequestEncrypted"/> holds it, written when the run record is added.</summary>
    public string? StoredRequest { get; init; }

    /// <summary>
    /// Who started the job, written to <see cref="MigrationRunEntity.StartedBySubjectId"/> and
    /// <see cref="MigrationRunEntity.StarterAccessEndsAt"/> when the run record is added.
    /// </summary>
    public CredentialMaker? Starter { get; init; }

    /// <summary>The <see cref="MigrationRunEntity.Revision"/> this job last wrote, or was resumed at.</summary>
    private int _revision;

    /// <summary>Set when the run record was found ended, as a cancel leaves it.</summary>
    private bool _recordEnded;

    /// <summary>Set when the run record was found live at a revision this job did not write.</summary>
    private bool _recordMoved;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The tenant that owns this migration job. Used to scope status/cancel lookups.</summary>
    public Guid TenantId => _tenantId;

    // A terminal state and the fields that describe it are written together under this lock, and
    // every reader copies them under it, so none sees a terminal state before the fields it carries.
    private readonly Lock _statusLock = new();
    private MigrationJobState _state = MigrationJobState.Pending;
    private string? _currentOperation;
    private MigrationPhase _phase = MigrationPhase.Connecting;
    private string? _currentCollection;
    private string? _errorMessage;
    private double _progressPercentage;
    private DateTime _startedAt;
    private DateTime? _completedAt;
    private readonly ConcurrentDictionary<string, CollectionProgress> _collectionProgress = new();

    /// <summary>
    /// Newest source timestamp this run actually brought across, for
    /// <see cref="MigrationSourceEntity.LastMigratedDataTimestamp"/>. Collections are pulled one
    /// after another on the job's own task, so a plain field suffices.
    /// </summary>
    private DateTime? _newestMigratedData;

    private static readonly System.Text.Json.JsonSerializerOptions s_caseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The one shape <see cref="MigrationRunEntity.CollectionOutcomes"/> is written and read in.
    /// Writing with one set of options and reading with another would silently lose every field.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions s_outcomeJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public MigrationJob(
        Guid id,
        Guid tenantId,
        StartMigrationRequest request,
        MigrationJobInfo info,
        TenantContext? tenantContext,
        ILogger logger,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider
    )
    {
        _id = id;
        _tenantId = tenantId;
        _request = request;
        _info = info;
        _tenantContext = tenantContext;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
    }

    /// <summary>Audit endpoint recorded for migration-owned writes.</summary>
    private const string AuditEndpoint = "service:migration";

    /// <summary>
    /// The DI scope migration work runs in: the owning tenant, with imported records attributed to
    /// the migration rather than a human actor, so a backfill writes no mutation_audit_log row per
    /// historical record.
    /// </summary>
    private AsyncServiceScope CreateTenantScope() =>
        TenantJobScope.Open(_serviceProvider, _tenantContext, AuditEndpoint);

    public MigrationJobStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new()
            {
                JobId = _id,
                State = _state,
                ProgressPercentage = _progressPercentage,
                CurrentOperation = _currentOperation,
                Phase = _state.IsLive() ? _phase : null,
                CurrentCollection = _state.IsLive() ? _currentCollection : null,
                ErrorMessage = _errorMessage,
                StartedAt = _startedAt,
                CompletedAt = _completedAt,
                EstimatedTimeRemaining = EstimateTimeRemaining(
                    _state, _documentWeighted, _pullStartedAt, _currentCollection, _collectionProgress, _progressPercentage, UtcNow),
                HasFailures = HasFailures(_state, _collectionProgress.Values),
                RecentDataReady = _recentDataReady || _state is MigrationJobState.Completed,
                CollectionProgress = _collectionProgress.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            };
        }
    }

    /// <summary>Set once the pass over <see cref="RecentSpan"/> has ended; see <see cref="MigrationJobStatus.RecentDataReady"/>.</summary>
    private volatile bool _recentDataReady;

    /// <summary>
    /// How far back from the end of its range an API run reads every collection before it reads the
    /// rest of any: long enough to fill the dashboard and the default report range.
    /// </summary>
    internal static readonly TimeSpan RecentSpan = TimeSpan.FromDays(14);

    /// <summary>The span <see cref="Passes"/> reads first, or null to read the range as one part.</summary>
    internal TimeSpan? ReadFirst { get; init; } = RecentSpan;

    private static readonly TimeSpan MinimumElapsedForEstimate = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The run's elapsed time scaled by the share of documents still to come, or
    /// <see langword="null"/> while that share is not known: before the pull starts, in its first
    /// seconds, for a source that did not count every collection it can, where progress steps by
    /// collection, and while the collection in flight has no total.
    /// </summary>
    private static TimeSpan? EstimateTimeRemaining(
        MigrationJobState state,
        bool documentWeighted,
        DateTime? pullStartedAt,
        string? currentCollection,
        IReadOnlyDictionary<string, CollectionProgress> collections,
        double progress,
        DateTime now)
    {
        if (state != MigrationJobState.Running || !documentWeighted || pullStartedAt is not { } started)
            return null;

        if (currentCollection is { } current
            && collections.TryGetValue(current, out var inFlight)
            && inFlight is { IsComplete: false, TotalDocuments: 0 })
        {
            return null;
        }

        var elapsed = now - started;
        if (elapsed < MinimumElapsedForEstimate || progress < 1)
            return null;

        return TimeSpan.FromSeconds(Math.Ceiling(elapsed.TotalSeconds * (100 - progress) / progress));
    }

    public void Cancel()
    {
        _cts.Cancel();
        // Leave terminal states untouched — cancelling a job that already Completed/Failed must
        // not rewrite its (now persisted) outcome, which would also un-satisfy the startup
        // "was this source ever migrated?" check.
        lock (_statusLock)
        {
            if (_state.IsLive())
            {
                _completedAt = UtcNow;
                _state = MigrationJobState.Cancelled;
            }
        }
    }

    /// <summary>How long this call runs before it stops at a checkpoint.</summary>
    private TimeSpan _slice;

    private long _sliceStarted;

    /// <summary>Whether this call has stored anything, so a slice of any length moves the run on.</summary>
    private bool _advanced;

    /// <summary>Set when this call stopped at a checkpoint with the run unfinished.</summary>
    private bool _paused;

    /// <summary>
    /// Runs the job from its checkpoint for up to <paramref name="slice"/>, then stops at the next
    /// checkpoint with the run still live.
    /// </summary>
    /// <returns>Whether the run is still live, so has more to do.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="externalCt"/> ended the call. The run stays live at its checkpoint, for whoever
    /// runs it next.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The run record is live at a revision this job did not write: another process holds the run,
    /// or a write of this job's landed without its knowing. The call stored nothing after finding
    /// that, and the run goes on from the record when it is next run.
    /// </exception>
    public async Task<bool> ExecuteAsync(TimeSpan slice, CancellationToken externalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token,
            externalCt
        );
        var ct = linkedCts.Token;
        (_slice, _sliceStarted, _advanced, _paused) = (slice, _timeProvider.GetTimestamp(), false, false);

        lock (_statusLock)
        {
            if (_state == MigrationJobState.Pending)
            {
                _startedAt = UtcNow;
                _state = MigrationJobState.Running;
            }
        }
        await PersistSnapshotAsync(CancellationToken.None);

        try
        {
            ct.ThrowIfCancellationRequested();

            if (_request.Mode == MigrationMode.Api)
            {
                await ExecuteApiMigrationAsync(ct);
            }
            else
            {
                await ExecuteMongoMigrationAsync(ct);
            }

            if (!_paused)
            {
                // A run that got some collections through stays Completed rather than gaining a state
                // the UI's badge switch does not know; the summary is what stops it reading as a clean
                // import.
                var summary = FailureSummary();
                lock (_statusLock)
                {
                    _errorMessage = summary;
                    _progressPercentage = 100;
                    _completedAt = UtcNow;
                    _state = MigrationJobState.Completed;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested || !externalCt.IsCancellationRequested)
        {
            lock (_statusLock)
            {
                _completedAt = UtcNow;
                _state = MigrationJobState.Cancelled;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex switch
            {
                // Already worded for the person who has to fix it; an inner transport message
                // appended to it would only add jargon.
                MigrationSourceException => ex.Message,
                { InnerException: not null } => $"{ex.Message} Inner: {ex.InnerException.Message}",
                _ => ex.Message,
            };
            lock (_statusLock)
            {
                _errorMessage = message;
                _completedAt = UtcNow;
                _state = MigrationJobState.Failed;
            }
            _logger.LogError(ex, "Migration job {JobId} failed", _id);
        }
        finally
        {
            await PersistSnapshotAsync(CancellationToken.None);
        }

        if (_recordMoved)
            throw new InvalidOperationException($"Migration job {_id} was written to by another holder; it goes on from its record.");

        lock (_statusLock)
        {
            return _state.IsLive();
        }
    }

    /// <summary>
    /// Stores the run's checkpoint and says whether the call stops here: its slice is used up and it
    /// has stored something. Everything the run has read is stored when this is called, so the next
    /// slice, or the process that takes the run over, goes on from here.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled, or the run record has ended or changed hands.</exception>
    private async Task<bool> PauseAsync(CancellationToken ct)
    {
        await StoreProgressAsync(ct);

        if (!_advanced || _timeProvider.GetElapsedTime(_sliceStarted) < _slice)
            return false;

        _paused = true;
        return true;
    }

    /// <summary>
    /// Stores the run's state, so its status is current and a cancel made in another process is
    /// seen, without offering the slice a place to stop.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled, or the run record has ended or changed hands.</exception>
    private async Task StoreProgressAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await PersistSnapshotAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Writes the job's state to its run record under the tenant's run lock. A record that has ended
    /// or changed hands (<see cref="WriteAsync"/>) is left alone and the job is cancelled. A job
    /// with no record yet propagates failures, since it must not start without one; later calls log
    /// instead, because the running work matters more than its record and the next call retries.
    /// </summary>
    public async Task PersistSnapshotAsync(CancellationToken ct)
    {
        MigrationJobState state;
        lock (_statusLock)
        {
            state = _state;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);

            var written = await db.ExecuteInTransactionAsync(async token =>
            {
                await db.LockForTransactionAsync(MigrationJobService.RunLockClass, _tenantId, token);
                var revision = await WriteAsync(db, token);
                if (revision is not null)
                    await db.SaveChangesAsync(token);
                return revision;
            }, ct: ct);

            if (written is { } stored)
            {
                _revision = stored;
            }
            else
            {
                _recordMoved = !_recordEnded;
                await _cts.CancelAsync();
            }
        }
        catch (Exception ex) when (state is not MigrationJobState.Pending)
        {
            _logger.LogError(ex, "Failed to persist migration job {JobId}", _id);
        }
    }

    /// <summary>
    /// Puts the job's state on its run record and migration source in <paramref name="db"/>, adding
    /// the record when the job has none. The caller holds the tenant's run lock, and saves.
    /// </summary>
    /// <returns>
    /// The revision the record now carries, or null when it was left alone: it has ended, as when it
    /// was cancelled, or another process has written to it since this job last did.
    /// </returns>
    internal async Task<int?> WriteAsync(NocturneDbContext db, CancellationToken ct)
    {
        MigrationJobState state;
        DateTime startedAt;
        DateTime? completedAt;
        string? errorMessage;
        lock (_statusLock)
        {
            (state, startedAt, completedAt, errorMessage) = (_state, _startedAt, _completedAt, _errorMessage);
        }

        var run = await db.MigrationRuns.FirstOrDefaultAsync(r => r.Id == _id, ct);
        if (run is not null && !MigrationJobStates.LiveNames.Contains(run.State))
        {
            _recordEnded = true;
            return null;
        }

        if (run is not null && run.Revision != _revision)
            return null;

        var revision = run is null ? _revision : _revision + 1;
        if (run is null)
        {
            run = new MigrationRunEntity
            {
                Id = _id,
                CreatedAt = _info.CreatedAt,
                RequestEncrypted = StoredRequest,
                StartedBySubjectId = Starter?.SubjectId,
                StarterAccessEndsAt = Starter?.AccessEndsAt,
            };
            db.MigrationRuns.Add(run);
        }

        var sourceId = await UpsertSourceAsync(db, ct);

        run.SourceId = sourceId;
        run.TenantId = _tenantId;
        run.Mode = _request.Mode.ToString();
        run.SourceDescription = _info.SourceDescription is { Length: > 512 } d ? d[..512] : _info.SourceDescription;
        run.State = state.ToString();
        run.StartedAt = startedAt == default ? _info.CreatedAt : startedAt;
        run.CompletedAt = completedAt;
        run.DateRangeStart = _request.StartDate?.AsUtc();
        run.DateRangeEnd = _request.EndDate?.AsUtc();
        run.ErrorMessage = errorMessage;
        run.EntriesMigrated = (int)Math.Min(int.MaxValue, MigratedCount("entries"));
        run.TreatmentsMigrated = (int)Math.Min(int.MaxValue, MigratedCount("treatments"));
        run.CollectionOutcomes = _collectionProgress.IsEmpty
            ? null
            : System.Text.Json.JsonSerializer.Serialize(_collectionProgress.Values.ToList(), s_outcomeJson);
        run.SourceAlarmSettings = _sourceAlarmSettings;
        run.Revision = revision;
        run.Checkpoint = state.IsLive()
            ? System.Text.Json.JsonSerializer.Serialize(ToCheckpoint(), s_outcomeJson)
            : null;
        if (!state.IsLive())
            run.RequestEncrypted = null;

        if (state == MigrationJobState.Completed)
        {
            var source = await db.MigrationSources.FirstAsync(s => s.Id == sourceId, ct);
            source.LastMigrationAt = completedAt ?? UtcNow;

            // A run bounded to an older date range covers ground an earlier run already
            // covered, so the watermark only ever advances.
            if (_newestMigratedData is { } newest
                && newest > (source.LastMigratedDataTimestamp ?? DateTime.MinValue))
            {
                source.LastMigratedDataTimestamp = newest;
            }
        }

        return revision;
    }

    /// <summary>
    /// What a live run stores in <see cref="MigrationRunEntity.Checkpoint"/>: where it is, what it
    /// has decided and counted on the way, and what it is doing, for the status of a run no process
    /// is holding. The per-collection outcomes are in <see cref="MigrationRunEntity.CollectionOutcomes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Pass</c> and <c>Next</c> are the part of the range and the collection the run reads next,
    /// and a paged collection's <see cref="PagedPull.Bound"/> is where in it. A step that pages on
    /// no time, and every MongoDB collection, is read again from its start.
    /// </para>
    /// <para>
    /// A live run outlives the version that started it, so a position is stored as what it names
    /// (<c>Split</c>, <c>Next</c>), never as an index into a list the code defines. A change to what
    /// any member means raises <see cref="CheckpointVersion"/>, and a run stored under another
    /// version starts over.
    /// </para>
    /// </remarks>
    internal sealed record Checkpoint
    {
        public int Version { get; init; }
        public bool Counted { get; init; }
        public DateTime WindowTo { get; init; }
        public DateTime? Split { get; init; }
        public int Pass { get; init; }
        public string? Next { get; init; }
        public bool DocumentWeighted { get; init; }
        public DateTime? PullStartedAt { get; init; }
        public bool RecentDataReady { get; init; }
        public List<string> Answered { get; init; } = [];
        public MigrationFailureCause? AbandonedAfter { get; init; }
        public DateTime? NewestMigratedData { get; init; }
        public Dictionary<string, PagedPull> Pulls { get; init; } = [];
        public double Progress { get; init; }
        public string? Operation { get; init; }
        public MigrationPhase Phase { get; init; }
        public string? Collection { get; init; }
    }

    internal const int CheckpointVersion = 1;

    private Checkpoint ToCheckpoint() => new()
    {
        Version = CheckpointVersion,
        Counted = _counted,
        WindowTo = _window.To,
        Split = _split,
        Pass = _pass,
        Next = _next,
        DocumentWeighted = _documentWeighted,
        PullStartedAt = _pullStartedAt,
        RecentDataReady = _recentDataReady,
        Answered = [.. _answered],
        AbandonedAfter = _abandonedAfter,
        NewestMigratedData = _newestMigratedData,
        Pulls = new(_pulls),
        Progress = _progressPercentage,
        Operation = _currentOperation,
        Phase = _phase,
        Collection = _currentCollection,
    };

    /// <summary>
    /// The checkpoint a run record holds, or null when it holds none, one this version cannot read
    /// or one of another <see cref="CheckpointVersion"/>; the run then starts over, which stores
    /// nothing twice.
    /// </summary>
    private static Checkpoint? ReadCheckpoint(MigrationRunEntity run)
    {
        if (string.IsNullOrEmpty(run.Checkpoint))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Checkpoint>(run.Checkpoint, s_outcomeJson)
                is { Version: CheckpointVersion } saved
                ? saved
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The job for a live run record, at the record's checkpoint.</summary>
    internal static MigrationJob Resume(
        MigrationRunEntity run,
        StartMigrationRequest request,
        TenantContext tenant,
        ILogger logger,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider)
    {
        var job = new MigrationJob(
            run.Id,
            run.TenantId,
            request,
            new MigrationJobInfo
            {
                Id = run.Id,
                Mode = request.Mode,
                CreatedAt = run.CreatedAt,
                SourceDescription = run.SourceDescription,
            },
            tenant,
            logger,
            serviceProvider,
            timeProvider);

        job._revision = run.Revision;
        job._state = Enum.TryParse<MigrationJobState>(run.State, out var state) ? state : MigrationJobState.Pending;
        job._startedAt = run.StartedAt;
        job._sourceAlarmSettings = run.SourceAlarmSettings;

        if (ReadCheckpoint(run) is not { } saved)
            return job;

        if (saved.Counted)
        {
            foreach (var (name, outcome) in OutcomesFromRecord(run, job._state))
                job._collectionProgress[name] = outcome;
        }

        job._counted = saved.Counted;
        job._window = new ImportWindow(request.StartDate?.AsUtc(), saved.WindowTo);
        job._split = saved.Split;
        job._pass = saved.Pass;
        job._next = saved.Next;
        job._documentWeighted = saved.DocumentWeighted;
        job._pullStartedAt = saved.PullStartedAt;
        job._recentDataReady = saved.RecentDataReady;
        job._answered.UnionWith(saved.Answered);
        job._abandonedAfter = saved.AbandonedAfter;
        job._newestMigratedData = saved.NewestMigratedData;
        foreach (var (name, pull) in saved.Pulls)
            job._pulls[name] = pull;
        job._progressPercentage = saved.Progress;
        job._currentOperation = saved.Operation;
        job._phase = saved.Phase;
        job._currentCollection = saved.Collection;
        return job;
    }

    private long MigratedCount(string collection) =>
        _collectionProgress.TryGetValue(collection, out var p) ? p.DocumentsMigrated : 0;

    private void ObserveDataWatermark(DateTime? candidate)
    {
        if (candidate is { } stamp && stamp > (_newestMigratedData ?? DateTime.MinValue))
            _newestMigratedData = stamp;
    }

    /// <summary>
    /// Finds or creates the migration source row for this job's target. Sources dedupe per
    /// tenant on <see cref="MigrationSourceEntity.SourceIdentifier"/>, a SHA-256 digest of the
    /// Nightscout base for API mode and of the connection string for MongoDB mode. Only non-usable
    /// digests and the <see cref="NightscoutAddress.Shown"/> URL are stored, never the API secret,
    /// a URL's token or path secret, or the connection string themselves.
    /// </summary>
    private async Task<Guid> UpsertSourceAsync(NocturneDbContext db, CancellationToken ct)
    {
        var isApi = _request.Mode == MigrationMode.Api;
        var identifier = isApi
            ? ApiSourceIdentifier(_request.NightscoutUrl!)
            : MongoSourceIdentifier(_request.MongoConnectionString ?? string.Empty);

        var existing = await db.MigrationSources
            .Where(s => s.TenantId == _tenantId && s.SourceIdentifier == identifier)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id)
            return id;

        var source = new MigrationSourceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Mode = _request.Mode.ToString(),
            SourceIdentifier = identifier,
            NightscoutUrl = isApi ? NightscoutAddress.Shown(_request.NightscoutUrl!) : null,
            NightscoutApiSecretHash = isApi && !string.IsNullOrEmpty(_request.NightscoutApiSecret)
                ? HashUtils.Sha256Hex(_request.NightscoutApiSecret)
                : null,
            MongoDatabaseName = isApi ? null : _request.MongoDatabaseName,
            CreatedAt = UtcNow,
        };
        db.MigrationSources.Add(source);
        return source.Id;
    }

    /// <summary>
    /// Canonical source identifier for an API-mode migration: a non-usable digest of the
    /// <see cref="NightscoutAddress.Display"/> base, never the URL itself. Shared with the startup
    /// pending-migration check so the two always agree.
    /// </summary>
    internal static string ApiSourceIdentifier(string nightscoutUrl) =>
        HashUtils.Sha256Hex(NightscoutAddress.Display(nightscoutUrl));

    /// <summary>Canonical source identifier for a MongoDB-mode migration: a non-usable digest, never the connection string itself.</summary>
    internal static string MongoSourceIdentifier(string connectionString) =>
        HashUtils.Sha256Hex(connectionString);

    /// <summary>A run record's status as of <paramref name="now"/>; a live run's from its checkpoint.</summary>
    public static MigrationJobStatus StatusFromRecord(MigrationRunEntity run, DateTime now)
    {
        var state = Enum.TryParse<MigrationJobState>(run.State, out var s)
            ? s
            : MigrationJobState.Interrupted;
        var outcomes = OutcomesFromRecord(run, state);
        var saved = state.IsLive() ? ReadCheckpoint(run) : null;
        var progress = state == MigrationJobState.Completed ? 100 : saved?.Progress ?? 0;

        return new MigrationJobStatus
        {
            JobId = run.Id,
            State = state,
            ProgressPercentage = progress,
            CurrentOperation = saved?.Operation,
            Phase = state.IsLive() ? saved?.Phase ?? MigrationPhase.Connecting : null,
            CurrentCollection = saved?.Collection,
            ErrorMessage = run.ErrorMessage,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            EstimatedTimeRemaining = saved is null
                ? null
                : EstimateTimeRemaining(
                    state, saved.DocumentWeighted, saved.PullStartedAt, saved.Collection, outcomes, progress, now),
            HasFailures = HasFailures(state, outcomes.Values),
            RecentDataReady = state == MigrationJobState.Completed || saved?.RecentDataReady == true,
            CollectionProgress = outcomes,
        };
    }

    /// <summary>
    /// Whether a run's message reports a fault. A Failed run's does whether or not a collection
    /// recorded one, since it can end before reaching any collection at all.
    /// </summary>
    private static bool HasFailures(MigrationJobState state, IEnumerable<CollectionProgress> collections) =>
        state is MigrationJobState.Failed || collections.Any(c => c.FailureReason is not null);

    /// <summary>
    /// Per-collection outcomes for a persisted run. Runs recorded before
    /// <see cref="MigrationRunEntity.CollectionOutcomes"/> existed carry only the two count
    /// columns, so those are reconstituted into the same shape.
    /// </summary>
    private static Dictionary<string, CollectionProgress> OutcomesFromRecord(
        MigrationRunEntity run, MigrationJobState state)
    {
        // The column is free-form to the database, so a row written by another version — or by
        // hand — must degrade to the counts below rather than break the status endpoint.
        try
        {
            var stored = string.IsNullOrEmpty(run.CollectionOutcomes)
                ? null
                : System.Text.Json.JsonSerializer
                    .Deserialize<List<CollectionProgress>>(run.CollectionOutcomes, s_outcomeJson);

            if (stored is { Count: > 0 })
                return stored.ToDictionary(c => c.CollectionName);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new Dictionary<string, CollectionProgress>
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
        };
    }

    /// <summary>Reconstructs a history entry from a persisted run.</summary>
    public static MigrationJobInfo InfoFromRecord(MigrationRunEntity run)
    {
        var state = Enum.TryParse<MigrationJobState>(run.State, out var s) ? s : MigrationJobState.Interrupted;

        return new MigrationJobInfo
        {
            Id = run.Id,
            Mode = Enum.TryParse<MigrationMode>(run.Mode, out var m) ? m : MigrationMode.Api,
            CreatedAt = run.CreatedAt,
            SourceDescription = run.SourceDescription,
            State = state,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            ErrorMessage = run.ErrorMessage,
            HasFailures = HasFailures(state, OutcomesFromRecord(run, state).Values),
        };
    }

    /// <summary>
    /// Whether overall progress is measured in documents. False until a source count or a MongoDB
    /// count gives the run a total; progress then steps by finished collection.
    /// </summary>
    private bool _documentWeighted;

    /// <summary>When the first collection started, after the counts, for <see cref="EstimateTimeRemaining"/>.</summary>
    private DateTime? _pullStartedAt;

    /// <summary>Whether the run has fixed <see cref="_window"/> and counted its collections, which it does once.</summary>
    private bool _counted;

    /// <summary>The index in <see cref="Passes"/> of the part of the range an API run reads next.</summary>
    private int _pass;

    /// <summary>The collection an API run reads next in <see cref="_pass"/>, or null for the pass's first.</summary>
    private string? _next;

    /// <summary>Where <see cref="Passes"/> divides <see cref="_window"/>, fixed with it; null for a range read as one part.</summary>
    private DateTime? _split;

    /// <summary>
    /// The time range this run imports: the requested dates, with the end no later than the moment
    /// the run started. One range for the whole run, so the counts and every pull agree on it.
    /// </summary>
    /// <remarks>
    /// The end is never open. Nightscout applies an implicit recency window (roughly the last four
    /// days) to any query carrying no date filter, which truncates an unbounded first page; the
    /// short page then ends pagination, importing days of history instead of years, and the same
    /// window makes an undated count a count of those days. Every request is explicitly dated.
    /// </remarks>
    private ImportWindow _window;

    /// <summary>A time range; <c>ToExclusive</c> leaves <c>To</c> itself outside it.</summary>
    private readonly record struct ImportWindow(DateTime? From, DateTime To, bool ToExclusive = false)
    {
        public bool Holds(DateTime instant) =>
            (From is null || instant >= From.Value) && (ToExclusive ? instant < To : instant <= To);
    }

    /// <summary>A range no instant is inside, so a cursor finds a record within it only when it cannot date the record.</summary>
    private static readonly ImportWindow s_noInstant = new(DateTime.MaxValue, DateTime.MinValue);

    /// <summary>
    /// One part of <see cref="_window"/>, read from every dated collection before the next part of
    /// any. <c>Last</c> says no part follows, so a collection read over this one is finished.
    /// </summary>
    private readonly record struct ImportPass(ImportWindow Window, bool Last);

    /// <summary>
    /// The parts of <see cref="_window"/> an API run reads in turn: the newest
    /// <see cref="RecentSpan"/>, then everything before it, so the app has current data long before
    /// a history of years has arrived. A range no longer than that span is one part.
    /// </summary>
    /// <remarks>
    /// The parts share no instant, and the boundary is a whole millisecond, which both cursors can
    /// write, so a record on it is read once.
    /// </remarks>
    private ImportWindow[] Passes() => _split is { } split
        ? [new ImportWindow(split, _window.To), new ImportWindow(_window.From, split, ToExclusive: true)]
        : [_window];

    /// <summary>The instant <see cref="ReadFirst"/> back from the end of <see cref="_window"/>, or null when the range starts no earlier.</summary>
    private DateTime? SplitOfWindow()
    {
        if (ReadFirst is not { } recent)
            return null;

        var split = _window.To - recent;
        split = split.AddTicks(-(split.Ticks % TimeSpan.TicksPerMillisecond));

        return _window.From is { } from && from >= split ? null : split;
    }

    /// <param name="anchored">
    /// Whether the end is held to the moment the run started. A MongoDB run reads the collection
    /// whole and needs no anchor, so without an end date it keeps a record dated after now.
    /// </param>
    private ImportWindow RequestedWindow(bool anchored)
    {
        var start = _request.StartDate?.AsUtc();
        var end = _request.EndDate?.AsUtc();
        if (!anchored)
            return new ImportWindow(start, end ?? DateTime.MaxValue);

        var now = UtcNow;
        return new ImportWindow(start, end is { } requested && requested < now ? requested : now);
    }

    /// <summary>
    /// The source's alarm settings as <see cref="NightscoutAlarmSettings.ToJson"/>, for
    /// <see cref="MigrationRunEntity.SourceAlarmSettings"/>. Null until read, and when the source
    /// would not give them: the settings pre-fill a form, so the run does not fail for them.
    /// </summary>
    private string? _sourceAlarmSettings;

    private async Task<string?> ReadAlarmSettingsAsync(HttpClient httpClient, CancellationToken ct)
    {
        try
        {
            var status = await ReadFromSourceAsync(httpClient, "api/v1/status.json", "status", ct, NightscoutRead.ImportProbe);
            return NightscoutAlarmSettings.Parse(status).ToJson();
        }
        catch (Exception ex) when (ex is MigrationSourceException or System.Text.Json.JsonException)
        {
            _logger.LogInformation(ex, "Migration job {JobId} read no alarm settings from the source", _id);
            return null;
        }
    }

    private async Task ExecuteApiMigrationAsync(CancellationToken ct)
    {
        using var scope = CreateTenantScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(MigrationJobService.HttpClientName);
        httpClient.BaseAddress = NightscoutAddress.TryResolve(_request.NightscoutUrl, out var address)
            ? address.BaseUri
            : throw new MigrationSourceException(NightscoutAddress.InvalidUrlMessage, MigrationFailureCause.Unreachable);

        // Add API secret header if provided (Nightscout expects the SHA1 hash)
        if (!string.IsNullOrEmpty(_request.NightscoutApiSecret))
        {
            httpClient.DefaultRequestHeaders.Add("api-secret", HashApiSecret(_request.NightscoutApiSecret));
        }

        await using var dbContext = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);

        // Build the list of collections to migrate
        // A dated collection is read once per pass; the others hold no time to split on.
        var allCollections = new (string name, bool dated, Func<HttpClient, ImportPass, CancellationToken, Task> migrate)[]
        {
            ("subjects", false, (client, _, token) => MigrateSubjectsViaApiAsync(
                client, dbContext, scope.ServiceProvider.GetRequiredService<IOAuthGrantRepository>(),
                scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
                scope.ServiceProvider.GetRequiredService<ITenantRepository>(), token)),
            ("entries", true, (client, pass, token) => MigratePagedCollectionAsync(client, s_entriesCollection, pass, token)),
            ("treatments", true, (client, pass, token) => MigratePagedCollectionAsync(client, s_treatmentsCollection, pass, token)),
            ("devicestatus", true, (client, pass, token) => MigratePagedCollectionAsync(client, s_deviceStatusCollection, pass, token)),
            ("profile", false, (client, _, token) => MigrateProfilesViaApiAsync(client, token)),
            ("food", false, (client, _, token) => MigrateFoodViaApiAsync(client, dbContext, token)),
            ("activity", true, (client, pass, token) => MigratePagedCollectionAsync(client, s_activityCollection, pass, token)),
        };

        var collectionsToMigrate = allCollections
            .Where(c => _request.Collections.Count == 0 || _request.Collections.Contains(c.name))
            .OrderBy(c => ImportRank(c.name))
            .ToList();

        if (!_counted)
        {
            _currentOperation = "Connecting to Nightscout";
            _sourceAlarmSettings = await ReadAlarmSettingsAsync(httpClient, ct);

            _currentOperation = "Counting records";
            _phase = MigrationPhase.Counting;
            _window = RequestedWindow(anchored: true);
            _split = SplitOfWindow();

            var counts = await Task.WhenAll(
                collectionsToMigrate.Select(c => FetchCollectionCountAsync(httpClient, c.name, ct)));
            for (var i = 0; i < collectionsToMigrate.Count; i++)
            {
                var name = collectionsToMigrate[i].name;
                _collectionProgress[name] = new CollectionProgress { CollectionName = name, TotalDocuments = counts[i] ?? 0 };
            }

            // A collection the source could have counted and did not would be weighed at what it has
            // served so far, which reads as nearly done for as long as it is pulled.
            _documentWeighted = counts.All(c => c is not null) && counts.Any(c => c > 0);
            _pullStartedAt = UtcNow;
            _counted = true;
        }

        var passes = Passes();
        for (var p = _pass; p < passes.Length; p++)
        {
            var pass = new ImportPass(passes[p], Last: p == passes.Length - 1);

            // A collection the run no longer lists starts the pass again.
            var first = _next is null ? 0 : Math.Max(0, collectionsToMigrate.FindIndex(c => c.name == _next));
            for (var s = first; s < collectionsToMigrate.Count; s++)
            {
                var (name, dated, migrate) = collectionsToMigrate[s];
                var following = s + 1 < collectionsToMigrate.Count ? collectionsToMigrate[s + 1].name : null;

                // A later pass reads only what is dated, and nothing more of a collection that failed.
                if (p > 0 && (!dated || Progress(name).IsComplete))
                {
                    _next = following;
                    continue;
                }

                _next = name;
                if (await PauseAsync(ct))
                    return;

                try
                {
                    await migrate(httpClient, pass, ct);
                    if (_paused)
                        return;

                    UpdateOverallProgress();
                    if (Progress(name).SkippedReason is null)
                        _answered.Add(name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Migration of {Collection} failed", name);

                    var failure = ex as MigrationSourceException ?? new MigrationSourceException(
                        $"Nocturne could not store the {name} it received.",
                        MigrationFailureCause.Internal,
                        ex);

                    RecordCollectionFailure(name, ReasonFor(failure));
                    UpdateOverallProgress();

                    if (IsFatal(failure.Cause))
                        throw new MigrationSourceException(failure.Message, failure.Cause);

                    if (IsConnectionLevel(failure.Cause))
                    {
                        // The connection, not this collection, is what failed. Trying the rest would
                        // spend a timeout each to arrive at the sentence already recorded.
                        _abandonedAfter = failure.Cause;
                        RecordHistoryAbandoned(failure.Cause);
                        return;
                    }
                }

                _next = following;
                _advanced = true;
            }

            // A collection that failed has no recent data to show, and the run's end is where
            // that is reported.
            if (!_collectionProgress.Values.Any(c => c.FailureReason is not null))
                _recentDataReady = true;

            (_pass, _next) = (p + 1, null);
        }
    }

    /// <summary>
    /// Gives each collection that had a pass read, and not its last, the reason its older history
    /// is missing. Without one it would be reported as not attempted, with its recent data in.
    /// </summary>
    private void RecordHistoryAbandoned(MigrationFailureCause cause)
    {
        var reason = cause is MigrationFailureCause.Unreachable
            ? "The older history was not imported because the connection to Nightscout had already failed."
            : "The older history was not imported because Nightscout had already refused this credential.";

        foreach (var name in _answered.Where(name => !Progress(name).IsComplete).ToList())
            RecordCollectionFailure(name, reason);
    }

    /// <summary>A cause that belongs to the connection, so every remaining collection would meet it too.</summary>
    private static bool IsConnectionLevel(MigrationFailureCause cause) =>
        cause is MigrationFailureCause.ApiSecretRejected or MigrationFailureCause.Unreachable;

    /// <summary>Set when a connection-level failure ended the run's remaining collections early.</summary>
    private MigrationFailureCause? _abandonedAfter;

    /// <summary>
    /// Whether <paramref name="cause"/> ends the whole run. Only while nothing has been imported:
    /// there is no partial success to preserve, and a connection-level cause defeats every
    /// remaining collection anyway. Once records are in, the run keeps them and completes — a
    /// connection-level cause then abandons the collections it has not reached rather than
    /// attempting each one.
    /// </summary>
    private bool IsFatal(MigrationFailureCause cause)
    {
        if (_collectionProgress.Values.Any(c => c.DocumentsMigrated > 0))
            return false;

        return IsConnectionLevel(cause) || !(_answered.Count > 0 || _collectionProgress.Values.Any(IsCleanCompletion));
    }

    /// <summary>
    /// The collections the source has served a pass of without a fault. A dated collection is not
    /// complete until its last pass, and a source with nothing recent imports nothing in the first,
    /// so this is what says the source was answering when a later collection fails.
    /// </summary>
    private readonly HashSet<string> _answered = [];

    /// <summary>
    /// A collection that was attempted and finished. A skip is excluded: Nightscout refusing an
    /// admin route says nothing about whether the rest of the source is answering.
    /// </summary>
    private static bool IsCleanCompletion(CollectionProgress c) =>
        c.IsComplete && c.FailureReason is null && c.SkippedReason is null;

    /// <summary>
    /// What to record against the collection that failed. A rejection arriving after other data
    /// has already come across is not the "check your API_SECRET" case — the secret demonstrably
    /// works — so it is worded as a limit on what this credential may read.
    /// </summary>
    private string ReasonFor(MigrationSourceException failure) =>
        failure.Cause is MigrationFailureCause.ApiSecretRejected
        && _collectionProgress.Values.Any(c => c.DocumentsMigrated > 0)
            ? PartialAccessMessage
            : failure.Message;

    /// <summary>Marks a collection finished-and-failed, keeping whatever it managed to import.</summary>
    internal void RecordCollectionFailure(string collectionName, string reason) =>
        _collectionProgress[collectionName] = Progress(collectionName) with
        {
            IsComplete = true,
            FailureReason = reason,
        };

    /// <summary>Marks a collection passed over rather than attempted. See <see cref="CollectionProgress.SkippedReason"/>.</summary>
    internal void RecordCollectionSkipped(string collectionName, string reason) =>
        _collectionProgress[collectionName] = Progress(collectionName) with
        {
            IsComplete = true,
            SkippedReason = reason,
        };

    private CollectionProgress Progress(string collectionName) =>
        _collectionProgress.TryGetValue(collectionName, out var existing)
            ? existing
            : new CollectionProgress { CollectionName = collectionName };

    /// <summary>
    /// How much of the run got through, each collection that did not and why, and — once — the
    /// reason any remaining collections were never attempted. <see langword="null"/> when every
    /// collection finished, so an untroubled run carries no message at all.
    /// </summary>
    /// <remarks>
    /// A collection is "not attempted" when it never completed and recorded no reason of its own:
    /// the run stopped before reaching it. Naming those separately is what keeps one connection
    /// failure from being reported as six. A skip is counted apart again — see
    /// <see cref="CollectionProgress.SkippedReason"/> — so a summary can be entirely untroubled.
    /// </remarks>
    private string? FailureSummary()
    {
        var failed = _collectionProgress.Values.Where(c => c.FailureReason is not null).ToList();
        var skipped = _collectionProgress.Values.Where(c => c.SkippedReason is not null).ToList();
        var notAttempted = _collectionProgress.Values
            .Count(c => c.FailureReason is null && c.SkippedReason is null && !c.IsComplete);

        if (failed.Count == 0 && skipped.Count == 0 && notAttempted == 0)
            return null;

        var total = _collectionProgress.Count;
        var counts = $"{total - failed.Count - skipped.Count - notAttempted} of {total} collections imported";
        if (failed.Count > 0)
            counts += $", {failed.Count} failed";
        if (skipped.Count > 0)
            counts += $", {skipped.Count} skipped";
        if (notAttempted > 0)
            counts += $", {notAttempted} not attempted";

        var detail = failed.Select(c => $"{c.CollectionName}: {c.FailureReason}").ToList();

        // A skip reason already names what was passed over, so prefixing the collection repeats it.
        detail.AddRange(skipped.Select(c => c.SkippedReason!));

        if (_abandonedAfter is { } cause && notAttempted > 0)
        {
            detail.Add(cause is MigrationFailureCause.Unreachable
                ? "The rest were not attempted because the connection to Nightscout had already failed."
                : "The rest were not attempted because Nightscout had already refused this credential.");
        }

        return $"{counts}. " + string.Join(" ", detail);
    }

    /// <summary>Reads one URL from the source and returns the body as a string.</summary>
    /// <remarks>
    /// <paramref name="read"/> says what this read is, for the wording a failure gets. Collections
    /// are the ordinary case and the default; the connection test names itself, because a 404 means
    /// something else there.
    /// </remarks>
    internal static Task<string> ReadFromSourceAsync(
        HttpClient httpClient, string url, string label, CancellationToken ct,
        NightscoutRead read = NightscoutRead.ImportCollection) =>
        ReadBodyFromSourceAsync(httpClient, url, label, read, (content, token) => content.ReadAsStringAsync(token), ct);

    /// <summary>Reads the body as UTF-8, regardless of the declared charset.</summary>
    internal static Task<T[]> ReadPageFromSourceAsync<T>(
        HttpClient httpClient, string url, string label, CancellationToken ct) =>
        ReadBodyFromSourceAsync(httpClient, url, label, NightscoutRead.ImportCollection, async (content, token) =>
        {
            await using var stream = await content.ReadAsStreamAsync(token);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T[]>(stream, cancellationToken: token) ?? [];
        }, ct);

    /// <summary>
    /// Reads one URL from the source, classifying every failure by what the user has to fix. The
    /// single place a migration read decides whether a response is usable, so that no page loop can
    /// mistake a rejection for the end of the data.
    /// </summary>
    private static async Task<T> ReadBodyFromSourceAsync<T>(
        HttpClient httpClient, string url, string label, NightscoutRead read,
        Func<HttpContent, CancellationToken, Task<T>> readBody, CancellationToken ct)
    {
        using var readToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Real time on purpose: it carries HttpClient.Timeout past the headers, and HttpClient measures that on the system clock.
        readToken.CancelAfter(httpClient.Timeout);
        using var response = await SendToSourceAsync(httpClient, url, label, read, readToken.Token, ct);
        try
        {
            return await readBody(response.Content, readToken.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new MigrationSourceException(NightscoutMessages.Unreachable, MigrationFailureCause.Unreachable, ex);
        }
    }

    private static async Task<HttpResponseMessage> SendToSourceAsync(
        HttpClient httpClient, string url, string label, NightscoutRead read,
        CancellationToken sendToken, CancellationToken jobToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(
                httpClient.BaseAddress is { } site ? NightscoutAddress.Of(site).For(url) : url,
                HttpCompletionOption.ResponseHeadersRead, sendToken);
        }
        catch (Nocturne.Infrastructure.Outbound.OutboundRefusedException ex)
        {
            throw new MigrationSourceException(ex.Message, MigrationFailureCause.Unreachable, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !jobToken.IsCancellationRequested)
        {
            throw new MigrationSourceException(NightscoutMessages.Unreachable, MigrationFailureCause.Unreachable, ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            // 403 is worded as a refusal rather than a rejected secret, but keeps the
            // ApiSecretRejected cause. That is how Nightscout's admin routes turn down a
            // non-admin secret, which the subjects step skips over rather than failing on.
            throw new MigrationSourceException(
                NightscoutMessages.ForStatus(
                    response.StatusCode, label, read, secretSent: httpClient.DefaultRequestHeaders.Contains("api-secret")),
                response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? MigrationFailureCause.ApiSecretRejected
                    : MigrationFailureCause.Status)
            {
                StatusCode = response.StatusCode,
            };
        }
    }

    private const string SubjectsNeedAdminSecretMessage =
        "Skipped: listing the people and devices that can sign in needs an admin API secret.";

    private const string NoProfileEndpointMessage =
        "Skipped: this Nightscout has no profile to hand over.";

    private const string PartialAccessMessage =
        "Nightscout refused to hand this over. The API secret was accepted for other data, so it "
        + "may not be allowed to read this.";

    /// <summary>How long a count may take before the run goes on without it.</summary>
    private static readonly TimeSpan CountBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many documents of a collection the run's window holds, by the source's count route: 0
    /// for a collection the route does not serve, and <see langword="null"/> when it serves the
    /// collection and gave no count.
    /// </summary>
    /// <remarks>
    /// The count carries the pull's own date filter; <see cref="_window"/> says what an undated one
    /// counts. A count is optional, so an error status or a count that outlasts
    /// <see cref="CountBudget"/> is tolerated: Nightscout versions that lack the route answer 404,
    /// and the pull that follows reports a real fault properly. A rejected secret or an
    /// unreachable host is not tolerated: those defeat every collection, so the run fails here
    /// with the right words.
    /// </remarks>
    internal async Task<long?> FetchCollectionCountAsync(
        HttpClient httpClient, string collectionName, CancellationToken ct)
    {
        if (!s_countableCollections.TryGetValue(collectionName, out var cursor))
            return 0;

        using var deadline = new CancellationTokenSource(CountBudget, _timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            var filter = WindowFilter(cursor, _window, _window.To + cursor.Envelope).TrimStart('&');
            var content = await ReadFromSourceAsync(
                httpClient, $"api/v1/count/{collectionName}/where?{filter}", collectionName, budget.Token);

            // Nightscout returns [{"_id": null, "count": N}], and [] when nothing matches.
            var results = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(content);
            return results is { Length: > 0 } && results[0].TryGetProperty("count", out var count)
                ? count.GetInt64()
                : 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Counting {Collection} took longer than {Budget}; continuing without a total", collectionName, CountBudget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && (ex as MigrationSourceException)?.Cause is null or MigrationFailureCause.Status)
        {
            _logger.LogWarning(ex, "Failed to fetch count for {Collection}, continuing without total", collectionName);
        }

        return null;
    }

    private void BeginCollection(string collectionName, string label)
    {
        _currentOperation = $"Migrating {label}";
        _currentCollection = collectionName;
        _phase = MigrationPhase.Importing;
    }

    /// <summary>The highest progress a run reports before it ends.</summary>
    private const double HighestRunningProgress = 99;

    private static long DocumentsSeen(CollectionProgress c) => c.DocumentsProcessed + c.DocumentsFailed;

    /// <summary>
    /// Sets the run's progress: documents dealt with over documents expected, where a collection
    /// in flight with no total is expected to hold what it has served so far. A run with no total
    /// at all (<see cref="_documentWeighted"/>) steps by finished collection instead, because the
    /// same sum would read complete from its first page.
    /// </summary>
    private void UpdateOverallProgress()
    {
        var collections = _collectionProgress.Values.ToList();
        if (collections.Count == 0)
            return;

        double share;
        if (_documentWeighted)
        {
            var seen = collections.Sum(DocumentsSeen);
            var expected = collections.Sum(c => c.IsComplete ? DocumentsSeen(c) : Math.Max(c.TotalDocuments, DocumentsSeen(c)));
            share = expected > 0 ? (double)seen / expected : 0;
        }
        else
        {
            share = (double)collections.Count(c => c.IsComplete) / collections.Count;
        }

        _progressPercentage = Math.Min(HighestRunningProgress, share * 100);
    }

    internal void UpdateCollectionProgress(
        string collectionName, long totalDocuments, long migrated, long failed, bool isComplete,
        DecompositionTally tally = default)
    {
        _collectionProgress[collectionName] = Progress(collectionName) with
        {
            TotalDocuments = totalDocuments,
            DocumentsMigrated = migrated,
            DocumentsFailed = failed,
            DocumentsSkippedUnsupported = tally.DocumentsSkippedUnsupported,
            DocumentsSkippedDeleted = tally.DocumentsSkippedDeleted,
            RecordsStored = tally.RecordsStored,
            RecordsSkippedDeleted = tally.RecordsSkippedDeleted,
            IsComplete = isComplete,
        };
    }

    /// <summary>What a collection's decompositions stored and passed over; see <see cref="DecompositionResult"/>.</summary>
    internal readonly record struct DecompositionTally(
        long RecordsStored, long RecordsSkippedDeleted, long DocumentsSkippedDeleted, long DocumentsSkippedUnsupported)
    {
        public long DocumentsSkipped => DocumentsSkippedDeleted + DocumentsSkippedUnsupported;

        /// <remarks>
        /// Set <paramref name="oneRecordPerDocument"/> when each document becomes exactly one
        /// record, so a deleted record is a skipped document.
        /// </remarks>
        public DecompositionTally Add(DecompositionResult result, bool oneRecordPerDocument) => new(
            RecordsStored + result.StoredRecords.Count(),
            RecordsSkippedDeleted + result.SkippedDeleted,
            DocumentsSkippedDeleted + (oneRecordPerDocument ? result.SkippedDeleted : 0),
            DocumentsSkippedUnsupported + result.SkippedUnsupported);
    }

    /// <summary>
    ///     Page size for every legacy-API pull. The merged v1 reads (devicestatus, activity) clamp
    ///     to <see cref="LegacyReadLimits.MaxMergedCount"/>, and the loops terminate on a short
    ///     page, so a larger value here would silently end those pulls after one page.
    /// </summary>
    private const int ApiPageSize = LegacyReadLimits.MaxMergedCount;

    /// <summary>
    ///     Page size for the device status pull. A looping uploader's device status carries its
    ///     predictions, around 13 KB a document, so <see cref="ApiPageSize"/> of them is a reply
    ///     past 100 MB, which a hosted Nightscout answers with a 502 and restarts.
    /// </summary>
    internal const int DeviceStatusPageSize = 1_000;

    /// <summary>
    ///     How a paged pull bounds and advances its time cursor: <paramref name="Filter"/> is the
    ///     query-string fragment restricting a page to records at or before the cursor,
    ///     <paramref name="Oldest"/> reads the page's oldest record, answering <c>null</c> when the
    ///     page carries no usable timestamp to page back from, <paramref name="AdmittedThrough"/>
    ///     is the latest record time the filter for a cursor admits, <paramref name="Envelope"/> is
    ///     how far outside the window the source is asked, <paramref name="LowerFilter"/> is the
    ///     fragment restricting a read to records at or after a time, <paramref name="Within"/> is
    ///     whether a record's instant is inside the window, and <paramref name="Newest"/> reads the
    ///     page's newest instant, for <see cref="MigrationSourceEntity.LastMigratedDataTimestamp"/>.
    /// </summary>
    private sealed record PageCursor(
        Func<DateTime, string> Filter,
        Func<IReadOnlyList<ProcessableDocumentBase>, DateTime?> Oldest,
        Func<DateTime, DateTime> AdmittedThrough,
        TimeSpan Envelope,
        Func<DateTime, string> LowerFilter,
        Func<ProcessableDocumentBase, ImportWindow, bool> Within,
        Func<IReadOnlyList<ProcessableDocumentBase>, DateTime?> Newest
    );

    /// <summary>
    ///     The date filter every read of a windowed collection carries, the count included:
    ///     <paramref name="upper"/> and, for a run with a start date, the window's lower edge, each
    ///     as far out as the cursor's envelope.
    /// </summary>
    private static string WindowFilter(PageCursor cursor, ImportWindow window, DateTime upper) =>
        cursor.Filter(upper) + (window.From is { } from ? cursor.LowerFilter(from - cursor.Envelope) : "");

    /// <summary>Entries page on the numeric <c>date</c> field, which mirrors mills exactly.</summary>
    private static readonly PageCursor s_dateCursor = new(
        to => $"&find[date][$lte]={to.ToMills()}",
        page =>
        {
            var dated = page.Where(d => d.Mills > 0).ToList();
            return dated.Count == 0
                ? null
                : dated.Min(d => d.Mills).ToUtcDateTime();
        },
        to => to,
        TimeSpan.Zero,
        from => $"&find[date][$gte]={from.ToMills()}",
        (d, window) => d.Mills <= 0 || window.Holds(d.Mills.ToUtcDateTime()),
        page => page.Any(d => d.Mills > 0)
            ? page.Max(d => d.Mills).ToUtcDateTime()
            : null);

    /// <summary>
    ///     Every other collection pages on the ISO-8601 <c>created_at</c> string, inside the
    ///     <see cref="BackwardTimePager.CreatedAtOffsetEnvelope"/>.
    /// </summary>
    private static readonly PageCursor s_createdAtCursor = new(
        to => $"&find[created_at][$lte]={BackwardTimePager.CreatedAtUpperBound(to)}",
        page => BackwardTimePager.OldestWrittenCreatedAt(page, d => d.CreatedAt),
        BackwardTimePager.CreatedAtAdmittedThrough,
        BackwardTimePager.CreatedAtOffsetEnvelope,
        // Millisecond precision sorts at or below both spellings of its instant ("...:00.000Z" and
        // "...:00Z") in a source that compares created_at as a string.
        from => $"&find[created_at][$gte]={from.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)}",
        // A value that will not parse is kept: a crawl never drops records it cannot date.
        (d, window) => !UploaderTimestamp.TryParse(d.CreatedAt, out var at) || window.Holds(at.UtcDateTime),
        page => page
            .Select(d => UploaderTimestamp.TryParse(d.CreatedAt, out var parsed)
                ? parsed.UtcDateTime
                : (DateTime?)null)
            .Max());

    /// <summary>
    ///     The collections Nightscout's count route serves, by the cursor their pull pages on. It
    ///     answers any other name with the count of entries.
    /// </summary>
    private static readonly Dictionary<string, PageCursor> s_countableCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["entries"] = s_dateCursor,
        ["treatments"] = s_createdAtCursor,
        ["devicestatus"] = s_createdAtCursor,
    };

    /// <summary>
    ///     A legacy collection pulled page by page over a time cursor. <paramref name="Name"/> is
    ///     both the v1 route segment and the progress key; <paramref name="Label"/> names the
    ///     records in operation and log text; <paramref name="OneRecordPerDocument"/> is whether each
    ///     document becomes exactly one record; <paramref name="Decompose"/> resolves the
    ///     collection's decomposer from the migration's tenant scope once per pull;
    ///     <paramref name="PageSize"/> is the count every page is first asked for.
    /// </summary>
    private sealed record PagedCollection<T>(
        string Name,
        string Label,
        PageCursor Cursor,
        bool OneRecordPerDocument,
        Func<IServiceProvider, Func<T[], CancellationToken, Task<DecompositionResult>>> Decompose,
        int PageSize = ApiPageSize
    ) where T : ProcessableDocumentBase;

    private static readonly PagedCollection<Entry> s_entriesCollection = new(
        "entries", "entries", s_dateCursor, OneRecordPerDocument: true,
        sp =>
        {
            var entries = sp.GetRequiredService<IEntryService>();
            return (page, ct) => entries.ImportEntriesAsync(page, ct);
        });

    private static readonly PagedCollection<Treatment> s_treatmentsCollection = new(
        "treatments", "treatments", s_createdAtCursor, OneRecordPerDocument: false,
        sp =>
        {
            var treatments = sp.GetRequiredService<ITreatmentService>();
            return (page, ct) => treatments.ImportTreatmentsAsync(page, ct);
        });

    private static readonly PagedCollection<DeviceStatus> s_deviceStatusCollection = new(
        "devicestatus", "device statuses", s_createdAtCursor, OneRecordPerDocument: false,
        sp =>
        {
            var deviceStatuses = sp.GetRequiredService<IDeviceStatusService>();
            return (page, ct) => deviceStatuses.ImportDeviceStatusesAsync(page, source: null, ct);
        },
        DeviceStatusPageSize);

    private static readonly PagedCollection<Activity> s_activityCollection = new(
        "activity", "activities", s_createdAtCursor, OneRecordPerDocument: false,
        sp =>
        {
            var decomposer = sp.GetRequiredService<IActivityDecomposer>();
            return async (page, ct) => await decomposer.DecomposeBatchAsync(page, WriteOrigin.Backfill, ct);
        });

    /// <summary>
    /// Reads each document on its own, so one Nocturne cannot read is counted failed instead of
    /// taking the rest of the collection with it. The log names only where the document sat: its
    /// body is health data.
    /// </summary>
    /// <remarks>
    /// The cursor pages back from the oldest document that was read, so an unreadable one older
    /// than that comes round again on the next page. <paramref name="failedIds"/> holds the
    /// <c>_id</c> of each failure so far, and one already in it is not counted again. A document
    /// with no <c>_id</c> cannot be recognised, so it is counted each time it is seen.
    /// </remarks>
    private (T[] Parsed, int NewlyFailed) ParseDocuments<T>(
        System.Text.Json.JsonElement[] documents, string label, int pageNumber, HashSet<string> failedIds)
        where T : class
    {
        var parsed = new List<T>(documents.Length);
        var newlyFailed = 0;
        for (var i = 0; i < documents.Length; i++)
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<T>(documents[i]) is { } document)
                {
                    parsed.Add(document);
                    continue;
                }

                _logger.LogWarning("Skipped {Collection} document {Index} on page {Page}: it was null", label, i, pageNumber);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    "Skipped {Collection} document {Index} on page {Page}: {Path} could not be read",
                    label, i, pageNumber, ex.Path);
            }

            var seenBefore = documents[i].ValueKind is System.Text.Json.JsonValueKind.Object
                && documents[i].TryGetProperty("_id", out var id)
                && !failedIds.Add(id.GetRawText());
            if (!seenBefore)
                newlyFailed++;
        }

        return ([.. parsed], newlyFailed);
    }

    /// <summary>
    /// What a paged collection has done so far, carried from one <see cref="ImportPass"/> to the next
    /// and, in <see cref="Checkpoint"/>, from one slice to the next.
    /// </summary>
    /// <remarks><c>KnownTotal</c> is the source's count over the whole of <see cref="_window"/>, or 0 with none.</remarks>
    internal sealed class PagedPull
    {
        public long KnownTotal { get; init; }
        public long Migrated { get; set; }
        public long Failed { get; set; }
        public long OutsideWindow { get; set; }
        public DecompositionTally Tally { get; set; }
        public int PageNumber { get; set; }

        /// <summary>
        /// The upper bound of the page the pass in flight reads next, every record newer than it
        /// stored; null between passes. A pass resumed from it is served the records at the bound's
        /// own second again, which are stored once and counted twice.
        /// </summary>
        public DateTime? Bound { get; set; }

        public HashSet<string> FailedIds { get; init; } = new(StringComparer.Ordinal);

        /// <summary>Ids of the records taken with no usable time. Every pass is served them, so they are taken once.</summary>
        public HashSet<string> UndatedIds { get; init; } = new(StringComparer.Ordinal);

        public HashSet<string> OutsideIds { get; init; } = new(StringComparer.Ordinal);

        [System.Text.Json.Serialization.JsonIgnore]
        public long Seen => Migrated + Failed + Tally.DocumentsSkipped;
    }

    private readonly Dictionary<string, PagedPull> _pulls = [];

    private async Task MigratePagedCollectionAsync<T>(
        HttpClient httpClient,
        PagedCollection<T> collection,
        ImportPass pass,
        CancellationToken ct
    ) where T : ProcessableDocumentBase
    {
        BeginCollection(collection.Name, collection.Label);
        if (!_pulls.TryGetValue(collection.Name, out var pull))
            _pulls[collection.Name] = pull = new PagedPull { KnownTotal = Progress(collection.Name).TotalDocuments };

        using var scope = CreateTenantScope();
        var decompose = collection.Decompose(scope.ServiceProvider);

        // The count reaches as far as the envelope, so what the window turns away comes off it.
        long Expected() => pull.KnownTotal > 0 ? Math.Max(pull.KnownTotal - pull.OutsideWindow, pull.Seen) : 0;

        var window = pass.Window;
        var pages = BackwardTimePager.PageAsync<T>(
            from: window.From - collection.Cursor.Envelope,
            to: pull.Bound ?? window.To + collection.Cursor.Envelope,
            collection.PageSize,
            BackwardTimePager.WidestPageSize(collection.PageSize),
            async (bound, count) =>
            {
                // A reply serving nothing ends the paging, which is how the slice stops here.
                pull.Bound = bound;
                if (await PauseAsync(ct))
                    return new TimePage<T>([], 0);

                pull.PageNumber++;

                var url = $"api/v1/{collection.Name}.json?count={count}"
                    + WindowFilter(collection.Cursor, window, bound ?? window.To + collection.Cursor.Envelope);

                var documents = await ReadPageFromSourceAsync<System.Text.Json.JsonElement>(
                    httpClient, url, collection.Label, ct);

                var (parsed, newlyFailed) = ParseDocuments<T>(documents, collection.Label, pull.PageNumber, pull.FailedIds);
                pull.Failed += newlyFailed;
                return new TimePage<T>(parsed, documents.Length);
            },
            // With no date on a full page to page back from, the cursor cannot move, and ending
            // here would drop every older document without saying so.
            page => collection.Cursor.Oldest(page) ?? throw new MigrationSourceException(
                $"Nightscout sent a page of {collection.Label} that Nocturne could not read, so the rest were not fetched.",
                MigrationFailureCause.Internal),
            collection.Cursor.AdmittedThrough,
            _logger,
            "Migration",
            collection.Name);

        await foreach (var served in pages)
        {
            var page = served
                .Where(d => collection.Cursor.Within(d, window)
                    && (!collection.Cursor.Within(d, s_noInstant) || d.Id is not { Length: > 0 } id || pull.UndatedIds.Add(id)))
                .ToArray();

            // Another pass takes what this one's part of the range turns away. Both passes can be
            // served a record just outside the whole range, so it is counted by id.
            pull.OutsideWindow += served.Count(d => !collection.Cursor.Within(d, _window)
                && (d.Id is not { Length: > 0 } id || pull.OutsideIds.Add(id)));
            if (page.Length > 0)
            {
                try
                {
                    var before = pull.Tally.DocumentsSkipped;
                    pull.Tally = pull.Tally.Add(await decompose(page, ct), collection.OneRecordPerDocument);
                    pull.Migrated += page.Length - (pull.Tally.DocumentsSkipped - before);
                    ObserveDataWatermark(collection.Cursor.Newest(page));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Failed to decompose {Collection} page", collection.Label);
                    pull.Failed += page.Length;
                }
            }

            UpdateCollectionProgress(collection.Name, Expected(), pull.Migrated, pull.Failed, false, pull.Tally);
            UpdateOverallProgress();
            _advanced = true;
        }

        if (_paused)
            return;

        pull.Bound = null;
        if (!pass.Last)
            return;

        // A finished collection holds what the source served, whatever it counted beforehand.
        UpdateCollectionProgress(collection.Name, pull.Seen, pull.Migrated, pull.Failed, true, pull.Tally);
        UpdateOverallProgress();
        if (pull.Seen < pull.KnownTotal - pull.OutsideWindow)
        {
            _logger.LogWarning(
                "Nightscout counted {Counted} {Collection} and served {Served}",
                pull.KnownTotal - pull.OutsideWindow, collection.Label, pull.Seen);
        }
        _logger.LogInformation(
            "Migrated {Count} {Collection} via API as {Stored} records, skipped {Unsupported} of an unsupported kind and {Deleted} deleted records",
            pull.Migrated, collection.Label, pull.Tally.RecordsStored, pull.Tally.DocumentsSkippedUnsupported, pull.Tally.RecordsSkippedDeleted);
    }

    private async Task MigrateProfilesViaApiAsync(
        HttpClient httpClient,
        CancellationToken ct
    )
    {
        var collectionName = "profile";
        BeginCollection(collectionName, "profiles");

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var tally = new DecompositionTally();

        System.Text.Json.JsonElement[] documents;
        try
        {
            documents = await ReadPageFromSourceAsync<System.Text.Json.JsonElement>(
                httpClient, "api/v1/profile.json", collectionName, ct);
        }
        catch (MigrationSourceException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Skipping profile migration: the source has no profile endpoint");
            RecordCollectionSkipped(collectionName, NoProfileEndpointMessage);
            return;
        }
        var (profiles, newlyFailed) = ParseDocuments<Profile>(documents, collectionName, 1, []);
        totalFailed += newlyFailed;

        UpdateCollectionProgress(collectionName, documents.Length, 0, totalFailed, false);
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

                tally = tally.Add(
                    await decomposer.DecomposeBatchAsync([profile], WriteOrigin.Backfill, ct), oneRecordPerDocument: false);
                totalMigrated++;
                UpdateCollectionProgress(collectionName, documents.Length, totalMigrated, totalFailed, false, tally);
                UpdateOverallProgress();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                totalFailed++;
            }
        }

        UpdateCollectionProgress(collectionName, documents.Length, totalMigrated, totalFailed, true, tally);
        UpdateOverallProgress();

        _logger.LogInformation(
            "Migrated {Count} profiles via API, skipped {Deleted} deleted records", totalMigrated, tally.RecordsSkippedDeleted);
    }

    private async Task MigrateFoodViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        const string collectionName = "food";
        BeginCollection(collectionName, "food");

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var totalSkipped = 0;
        var failedIds = new HashSet<string>(StringComparer.Ordinal);
        var servedIds = new HashSet<string>(StringComparer.Ordinal);
        string[]? previousPage = null;

        for (var pageNumber = 1; ; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"api/v1/food.json?count={ApiPageSize}&skip={totalSkipped}";
            var documents = await ReadPageFromSourceAsync<System.Text.Json.JsonElement>(
                httpClient, url, collectionName, ct);

            if (documents.Length == 0) break;

            // Nightscout serves its whole food list whatever skip says, so a list of a full page or
            // more comes back again for every skip. The same page twice is the end.
            var served = documents.Select(d => d.GetRawText()).ToArray();
            if (previousPage is not null && served.SequenceEqual(previousPage)) break;
            previousPage = served;

            var (parsed, newlyFailed) = ParseDocuments<Food>(documents, collectionName, pageNumber, failedIds);
            totalFailed += newlyFailed;

            var foods = parsed.Where(f => f.Id is not { Length: > 0 } id || servedIds.Add(id)).ToArray();

            foreach (var food in foods)
            {
                try
                {
                    if (!await FoodImportBlockedAsync(dbContext, food.Id, food.Name ?? "", food.Type ?? "food", ct))
                    {
                        dbContext.Foods.Add(
                            new Infrastructure.Data.Entities.FoodEntity
                            {
                                Id = Guid.CreateVersion7(),
                                OriginalId = food.Id,
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
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    totalFailed++;
                }
            }

            await dbContext.SaveChangesAsync(ct);
            totalSkipped += documents.Length;

            UpdateCollectionProgress(collectionName, 0, totalMigrated, totalFailed, false);
            UpdateOverallProgress();

            if (documents.Length < ApiPageSize) break;
        }

        UpdateCollectionProgress(collectionName, totalMigrated + totalFailed, totalMigrated, totalFailed, true);
        UpdateOverallProgress();

        _logger.LogInformation("Migrated {Count} food items via API", totalMigrated);
    }

    /// <summary>
    /// Where a collection falls in the import: subjects first, then entries and treatments, then the
    /// rest in their listed order, the Mongo listing included. The profile lands after the
    /// treatments, so its write re-resolves every percent temp basal they hold
    /// (<see cref="V4.TempBasalRateReresolver"/>).
    /// </summary>
    internal static int ImportRank(string collection) => collection switch
    {
        "subjects" => 0,
        "entries" or "treatments" => 1,
        _ => 2,
    };

    private async Task ExecuteMongoMigrationAsync(CancellationToken ct)
    {
        if (!_counted)
            _currentOperation = "Connecting to MongoDB";

        var client = new MongoClient(_request.MongoConnectionString);
        var database = client.GetDatabase(_request.MongoDatabaseName);

        using var scope = CreateTenantScope();
        await using var dbContext = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync(ct);

        // List available collections
        var collections = await database.ListCollectionNamesAsync(cancellationToken: ct);
        var collectionList = await collections.ToListAsync(ct);

        // Filter to requested collections
        var collectionsToMigrate =
            (_request.Collections.Count > 0
                ? collectionList.Where(c => _request.Collections.Contains(c))
                : collectionList.Where(ImportableFromMongo))
            .OrderBy(ImportRank)
            .ToList();

        if (!_counted)
        {
            _currentOperation = "Counting records";
            _phase = MigrationPhase.Counting;
            _window = RequestedWindow(anchored: false);
            foreach (var collectionName in collectionsToMigrate)
                _collectionProgress[collectionName] = await CountedAsync(collectionName);
            _documentWeighted = true;
            _pullStartedAt = UtcNow;
            _counted = true;
        }

        // A collection is read whole, so a resumed run reads the one it was in again and leaves the
        // finished ones alone.
        foreach (var collectionName in collectionsToMigrate.Where(c => _collectionProgress.TryGetValue(c, out var done) && !done.IsComplete))
        {
            if (await PauseAsync(ct))
                return;

            // Read again, it is tallied again, against a fresh count.
            if (DocumentsSeen(Progress(collectionName)) > 0)
                _collectionProgress[collectionName] = await CountedAsync(collectionName);

            BeginCollection(collectionName, collectionName);

            switch (collectionName)
            {
                case "entries":
                    await MigrateMongoDecomposedCollectionAsync(database, s_entriesCollection, ct);
                    break;
                case "treatments":
                    await MigrateMongoDecomposedCollectionAsync(database, s_treatmentsCollection, ct);
                    break;
                case "activity":
                    await MigrateMongoDecomposedCollectionAsync(database, s_activityCollection, ct);
                    break;
                case "devicestatus" or "profile" or "food":
                    await MigrateMongoCollectionAsync(database, collectionName, dbContext, ct);
                    break;
                default:
                    _logger.LogWarning("MongoDB migration cannot import the {Collection} collection; skipping it", collectionName);
                    RecordCollectionSkipped(collectionName, $"{collectionName} cannot be imported in MongoDB mode.");
                    break;
            }

            UpdateOverallProgress();
            _advanced = true;
        }

        async Task<CollectionProgress> CountedAsync(string collectionName) => new()
        {
            CollectionName = collectionName,
            TotalDocuments = ImportableFromMongo(collectionName)
                ? await database.GetCollection<BsonDocument>(collectionName)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct)
                : 0,
        };
    }

    private static bool ImportableFromMongo(string collection) =>
        collection is "entries" or "treatments" or "devicestatus" or "profile" or "food" or "activity";

    /// <summary>The instant of a stored created_at, which MongoDB holds as a string or as a date.</summary>
    private bool CreatedAtWithinWindow(BsonValue createdAt) => createdAt switch
    {
        { IsString: true } => BackwardTimePager.CreatedAtWithin(createdAt.AsString, _window.From, _window.To),
        { IsValidDateTime: true } => _window.Holds(createdAt.ToUniversalTime()),
        _ => true,
    };

    /// <summary>
    /// Streams a MongoDB collection through the parsing and decomposer of the API path's
    /// <see cref="MigratePagedCollectionAsync{T}"/>, so a document counts as migrated only when
    /// its decomposition stored it.
    /// </summary>
    private async Task MigrateMongoDecomposedCollectionAsync<T>(
        IMongoDatabase database,
        PagedCollection<T> collection,
        CancellationToken ct
    ) where T : ProcessableDocumentBase
    {
        var source = database.GetCollection<BsonDocument>(collection.Name);
        var totalDocs = Progress(collection.Name).TotalDocuments;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var outsideWindow = 0L;
        var window = _window;
        var tally = new DecompositionTally();
        var failedIds = new HashSet<string>(StringComparer.Ordinal);

        using var scope = CreateTenantScope();
        var decompose = collection.Decompose(scope.ServiceProvider);

        using var cursor = await source.FindAsync(
            FilterDefinition<BsonDocument>.Empty,
            new FindOptions<BsonDocument> { BatchSize = ApiPageSize },
            ct);

        for (var batchNumber = 1; await cursor.MoveNextAsync(ct); batchNumber++)
        {
            var documents = new List<System.Text.Json.JsonElement>(cursor.Current.Count());
            var index = 0;
            foreach (var bson in cursor.Current)
            {
                try
                {
                    documents.Add(ToNightscoutJson(bson));
                }
                catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
                {
                    _logger.LogWarning(
                        "Skipped {Collection} document {Index} in batch {Batch}: a decimal or date is out of range",
                        collection.Label, index, batchNumber);
                    totalFailed++;
                }
                index++;
            }
            if (documents.Count == 0) continue;

            var (parsed, newlyFailed) = ParseDocuments<T>([.. documents], collection.Label, batchNumber, failedIds);
            totalFailed += newlyFailed;

            // The collection is streamed whole, so the count holds what the window turns away.
            var page = parsed.Where(d => collection.Cursor.Within(d, window)).ToArray();
            outsideWindow += parsed.Length - page.Length;

            if (page.Length > 0)
            {
                try
                {
                    var before = tally.DocumentsSkipped;
                    tally = tally.Add(await decompose(page, ct), collection.OneRecordPerDocument);
                    totalMigrated += page.Length - (tally.DocumentsSkipped - before);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to decompose {Collection} batch", collection.Label);
                    totalFailed += page.Length;
                }
            }

            UpdateCollectionProgress(collection.Name,
                Math.Max(totalDocs - outsideWindow, totalMigrated + totalFailed + tally.DocumentsSkipped),
                totalMigrated, totalFailed, false, tally);
            UpdateOverallProgress();
            await StoreProgressAsync(ct);
        }

        UpdateCollectionProgress(collection.Name,
            totalMigrated + totalFailed + tally.DocumentsSkipped, totalMigrated, totalFailed, true, tally);
        _logger.LogInformation(
            "Migrated {Count} {Collection} from MongoDB as {Stored} records, skipped {Unsupported} of an unsupported kind and {Deleted} deleted records",
            totalMigrated, collection.Label, tally.RecordsStored, tally.DocumentsSkippedUnsupported, tally.RecordsSkippedDeleted);
    }

    /// <summary>
    /// Renders a stored document the way Nightscout's API serves it: an ObjectId as its hex string
    /// and a BSON date as an ISO-8601 string, which is what the domain models read.
    /// </summary>
    /// <exception cref="OverflowException">A Decimal128 lies outside the range of <see cref="decimal"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A BSON date lies outside the range of <see cref="DateTime"/>.</exception>
    internal static System.Text.Json.JsonElement ToNightscoutJson(BsonDocument document) =>
        System.Text.Json.JsonSerializer.SerializeToElement(ToJsonNode(document));

    internal static System.Text.Json.Nodes.JsonNode? ToJsonNode(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => new System.Text.Json.Nodes.JsonObject(
            value.AsBsonDocument.Select(e => KeyValuePair.Create(e.Name, ToJsonNode(e.Value)))),
        BsonType.Array => new System.Text.Json.Nodes.JsonArray([.. value.AsBsonArray.Select(ToJsonNode)]),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        BsonType.String => value.AsString,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        // JSON has no NaN or Infinity; JSON.stringify writes them as null.
        BsonType.Double => double.IsFinite(value.AsDouble) ? value.AsDouble : null,
        BsonType.Decimal128 => (decimal)value.AsDecimal128,
        BsonType.Null or BsonType.Undefined => null,
        _ => value.ToString(),
    };

    private async Task MigrateMongoCollectionAsync(
        IMongoDatabase database,
        string collectionName,
        NocturneDbContext dbContext,
        CancellationToken ct
    )
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var totalDocs = Progress(collectionName).TotalDocuments;

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var outsideWindow = 0L;
        var windowed = collectionName == "devicestatus";
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
                if (windowed && doc.TryGetValue("created_at", out var createdAt) && !CreatedAtWithinWindow(createdAt))
                {
                    outsideWindow++;
                    continue;
                }

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

            _collectionProgress[collectionName] = Progress(collectionName) with
            {
                TotalDocuments = Math.Max(totalDocs - outsideWindow, totalMigrated + totalFailed),
                DocumentsMigrated = totalMigrated,
                DocumentsFailed = totalFailed,
                IsComplete = false,
            };
            UpdateOverallProgress();
            await StoreProgressAsync(ct);
        }

        _collectionProgress[collectionName] = Progress(collectionName) with
        {
            TotalDocuments = totalMigrated + totalFailed,
            DocumentsMigrated = totalMigrated,
            DocumentsFailed = totalFailed,
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
                throw new ArgumentOutOfRangeException(nameof(collectionName), collectionName, null);
        }
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
        var status = System.Text.Json.JsonSerializer.Deserialize<DeviceStatus>(json, JsonDefaults.CaseInsensitive);

        if (status == null)
            return;

        // Set the original ID from MongoDB _id
        if (doc.Contains("_id"))
            status.Id = doc["_id"].AsObjectId.ToString();

        using var scope = CreateTenantScope();
        await scope.ServiceProvider.GetRequiredService<IDeviceStatusService>()
            .CreateDeviceStatusesAsync([status], source: null, WriteOrigin.Backfill, ct);
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
              && UploaderTimestamp.TryParse(doc["created_at"].AsString, out var createdAt)
                ? createdAt.ToUnixTimeMilliseconds()
            : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

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
            StartDate = doc.GetValue("startDate", BsonNull.Value) switch
            {
                { IsString: true } value => value.AsString,
                { IsValidDateTime: true } value => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                _ => null,
            },
            Mills = mills,
            CreatedAt = doc.Contains("created_at") ? doc["created_at"].AsString : null,
            Units = doc.Contains("units") ? doc["units"].AsString : "mg/dl",
            Store = store,
            EnteredBy = doc.Contains("enteredBy") ? doc["enteredBy"].AsString : null,
            LoopSettings = loopSettings,
            IsAPNSProduction = doc.Contains("isAPNSProduction") && doc["isAPNSProduction"].IsBoolean
                ? doc["isAPNSProduction"].AsBoolean
                : null,
        };

        using var scope = CreateTenantScope();
        var decomposer = scope.ServiceProvider.GetRequiredService<Nocturne.Core.Contracts.V4.IProfileDecomposer>();
        await decomposer.DecomposeBatchAsync([profile], WriteOrigin.Backfill, ct);
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
        if (await FoodImportBlockedAsync(dbContext, originalId, name, type, ct))
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
    /// Whether a migrated food is already held: live, or deleted by the user, whose delete a
    /// re-import must not undo (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </summary>
    internal static Task<bool> FoodImportBlockedAsync(
        NocturneDbContext dbContext, string? originalId, string name, string type, CancellationToken ct) =>
        dbContext.Foods.IncludingDeleted()
            .Where(f => (originalId != null && f.OriginalId == originalId) || (f.Name == name && f.Type == type))
            .WhereBlocksRecreation()
            .AnyAsync(ct);

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

    /// <summary>
    /// The maker the run record holds for the tokens it adopts. It is read from the record, not from
    /// <see cref="Starter"/>, so a resumed run stamps the end captured at start, lowered by any cap
    /// since (<see cref="MemberCreations.CapAsync"/>); null for a run that recorded no starter.
    /// </summary>
    private async Task<CredentialMaker?> ReadStarterAsync(NocturneDbContext dbContext, CancellationToken ct)
    {
        var run = await dbContext.MigrationRuns.AsNoTracking()
            .Where(r => r.Id == _id && r.TenantId == _tenantId)
            .Select(r => new { r.StartedBySubjectId, r.StarterAccessEndsAt })
            .FirstOrDefaultAsync(ct);
        return run?.StartedBySubjectId is { } subjectId ? new CredentialMaker(subjectId, run.StarterAccessEndsAt) : null;
    }

    private async Task MigrateSubjectsViaApiAsync(
        HttpClient httpClient,
        NocturneDbContext dbContext,
        IOAuthGrantRepository grants,
        IRoleRepository roleCatalogue,
        ITenantRepository tenants,
        CancellationToken ct)
    {
        var collectionName = "subjects";
        BeginCollection(collectionName, "subjects");

        var totalMigrated = 0L;
        var totalFailed = 0L;
        var totalSkipped = 0L;

        // 1. Fetch roles to build name->permissions lookup
        var rolePermissions = await FetchNightscoutRolePermissionsAsync(httpClient, ct);

        // 2. Fetch subjects
        string content;
        try
        {
            content = await ReadFromSourceAsync(
                httpClient, "api/v2/authorization/subjects", collectionName, ct);
        }
        catch (MigrationSourceException ex) when (ex.Cause is not MigrationFailureCause.Unreachable)
        {
            if (ex.Cause is MigrationFailureCause.ApiSecretRejected)
            {
                _logger.LogInformation(
                    "Skipping subject migration: the API secret lacks admin access ({Reason})", ex.Message);
                RecordCollectionSkipped(collectionName, SubjectsNeedAdminSecretMessage);
            }
            else
            {
                _logger.LogWarning("Failed to fetch subjects: {Reason}", ex.Message);
                RecordCollectionFailure(collectionName, ex.Message);
            }

            return;
        }

        var subjects = System.Text.Json.JsonSerializer.Deserialize<NightscoutSubject[]>(
            content,
            s_caseInsensitiveJson) ?? [];

        UpdateCollectionProgress(collectionName, subjects.Length, 0, 0, false);
        UpdateOverallProgress();

        var existingHashes = (await grants.GetTokenHashesAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var nocturneRoles = (await roleCatalogue.ListAsync(ct)).ToDictionary(r => r.Name, r => r);
        var addsMissingRoles = await Auth.InstanceStanding.IsOnlyTenantAsync(tenants, _tenantId, ct);

        // Nightscout derives each subject's token from digest = sha1(sha1(api_secret) + _id).
        // HashApiSecret yields exactly that inner sha1(api_secret), so with the mongo _id we
        // can reconstruct the full 40-char digest and store it for 1:1 legacy token matching.
        var hashedSecret = string.IsNullOrEmpty(_request.NightscoutApiSecret)
            ? null
            : HashApiSecret(_request.NightscoutApiSecret);

        Guid? deviceSubjectId = null;
        var starter = await ReadStarterAsync(dbContext, ct);

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

                // "denied" is Nightscout's way of spelling "no access". Importing a working
                // credential for it would hand out access the source instance had taken away, so the
                // subject is passed over rather than imported as a revoked grant.
                if (subject.Roles is ["denied"])
                {
                    totalSkipped++;
                    continue;
                }

                if (addsMissingRoles)
                    await AddMissingRolesAsync(roleCatalogue, nocturneRoles, rolePermissions, subject.Roles, ct);
                var scopes = ScopeTranslator.FromPermissions(GrantedPermissions(subject.Roles, nocturneRoles, rolePermissions));

                // A subject whose permissions all fall outside Nocturne's vocabulary would get a
                // credential that authorizes nothing. Skip it rather than leave a token on the list
                // that fails every request it is used for.
                if (scopes.Count == 0)
                {
                    totalSkipped++;
                    continue;
                }

                var mongoId = subject.MongoId ?? subject.Id;
                var legacyDigest = Auth.LegacyNightscoutToken.DeriveDigest(hashedSecret, mongoId, subject.AccessToken);

                // Resolved on the first token actually worth importing, so a run that converts
                // nothing leaves no holder behind. See OrphanedSubjectFilter for what an account
                // with no way to sign in costs the tenant, and DeviceSubjectFilter for why the
                // holder is not a person.
                deviceSubjectId ??= await dbContext.DeviceSubjectOf(_tenantId, ct);

                // Both spellings the source instance would have accepted: the token verbatim, and
                // any other digest prefix. Existing AAPS and xDrip setups keep uploading.
                await grants.AdoptLegacyCredentialAsync(
                    new AdoptedLegacyCredential(
                        deviceSubjectId.Value,
                        subject.Name ?? "Unnamed",
                        [.. scopes],
                        TokenHash: tokenHash,
                        LegacyTokenDigest: legacyDigest,
                        CreatedBySubjectId: starter?.SubjectId,
                        ExpiresAt: starter?.AccessEndsAt),
                    ct);

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

        _logger.LogInformation(
            "Subject migration complete: {Migrated} migrated, {Skipped} skipped, {Failed} failed",
            totalMigrated, totalSkipped, totalFailed);
    }

    /// <summary>
    /// Adds each of a subject's role names the instance lacks to the global roles, with the source's
    /// definition, so a later edit through the V2 subjects API can name it. The grant's scopes do not
    /// depend on it (<see cref="GrantedPermissions"/>). Every tenant reads the global roles, so the
    /// caller adds them only on the instance's only tenant
    /// (<see cref="Auth.InstanceStanding.IsOnlyTenantAsync"/>): the job holds no record of whether whoever
    /// started it had standing over the instance. <paramref name="knownRoles"/> is updated so each
    /// role is added once per run.
    /// </summary>
    /// <remarks>
    /// "denied" is skipped: it is Nightscout's way of spelling "no access", carried instead by the
    /// subject's <c>is_active</c> flag. <see cref="PlatformAdminCredentials.RoleName"/> is reserved.
    /// </remarks>
    private static async Task AddMissingRolesAsync(
        IRoleRepository roleCatalogue,
        Dictionary<string, AuthRole> knownRoles,
        Dictionary<string, List<string>> sourcePermissions,
        List<string>? roleNames,
        CancellationToken ct)
    {
        foreach (var roleName in roleNames ?? [])
        {
            if (roleName == "denied" || PlatformAdminCredentials.IsRoleName(roleName) || knownRoles.ContainsKey(roleName))
                continue;

            knownRoles[roleName] = await roleCatalogue.CreateAsync(
                new NewRole(roleName, "Migrated from Nightscout", sourcePermissions.GetValueOrDefault(roleName, []), IsSystemRole: false),
                ct);
        }
    }

    /// <summary>
    /// The legacy permissions a subject's roles grant it on the source instance. The source's own
    /// definition wins over the same-named Nocturne role, which may be a wider seeded role that
    /// merely shares a name (a hand-written Nightscout role called <c>admin</c> need not mean
    /// <c>*</c>). The local definition is the fallback for a source whose roles endpoint was
    /// inaccessible. "denied" grants nothing.
    /// </summary>
    private static IEnumerable<string> GrantedPermissions(
        List<string>? roleNames, Dictionary<string, AuthRole> knownRoles, Dictionary<string, List<string>> sourcePermissions) =>
        (roleNames ?? []).Where(name => name != "denied").SelectMany(name =>
            sourcePermissions.TryGetValue(name, out var fromSource) ? fromSource
            : knownRoles.TryGetValue(name, out var local) ? local.Permissions
            : []);

    /// <summary>
    /// Fetches Nightscout role definitions and returns a name-to-permissions lookup.
    /// Falls back gracefully if the endpoint is inaccessible.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> FetchNightscoutRolePermissionsAsync(
        HttpClient httpClient, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        try
        {
            var content = await ReadFromSourceAsync(
                httpClient, "api/v2/authorization/roles", "roles", ct);
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
