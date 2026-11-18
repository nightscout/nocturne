using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Oura.Configurations;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Health.Oura;

/// <summary>
/// Runs an Oura export import as a background job, one per tenant at a time, the way
/// <see cref="Migration.MigrationJobService"/> runs a migration: a <see cref="TenantRunGuard"/> lease
/// held for the whole run, and a detached task that outlives the upload request.
/// </summary>
/// <remarks>
/// Only each tenant's latest job is kept, in memory. After a restart there is none, and the page says
/// so: importing the same export again completes whatever an interrupted run had written.
/// </remarks>
public sealed class OuraImportJobService(
    IServiceProvider services,
    TenantRunGuard runGuard,
    TimeProvider time,
    ILogger<OuraImportJobService> logger)
{
    /// <summary>The run key the import and <see cref="OuraImportPurgeGuard"/> share, so a delete never races an import.</summary>
    internal const string RunName = "oura-import";

    private const string AuditEndpoint = "service:oura-import";

    /// <summary>The error code of a job that ended on anything but an <see cref="OuraExportException"/>.</summary>
    public const string ImportFailedCode = "import_failed";

    private readonly ConcurrentDictionary<Guid, OuraImportJob> _latest = new();

    /// <summary>
    /// Starts importing <paramref name="export"/> for <paramref name="reader"/>, who must have a tenant.
    /// The caller has checked the export with <see cref="OuraExportArchive.Validate"/>.
    /// </summary>
    /// <exception cref="OuraImportBlockedException">The tenant's import or a delete of its Oura data is running.</exception>
    public OuraImportStatus Start(byte[] export, TenantReader reader)
    {
        if (reader.Tenant is not { TenantId: var tenantId } || tenantId == Guid.Empty)
            throw new InvalidOperationException("An Oura import writes under a tenant; refusing to start without one.");

        var job = new OuraImportJob(Guid.CreateVersion7(), time);
        IDisposable? lease;
        while ((lease = runGuard.TryAcquire(tenantId, RunName, job.Id)) is null)
        {
            if (runGuard.TryGetHolder(tenantId, RunName, out var holder))
                throw new OuraImportBlockedException(byDelete: holder == Guid.Empty);
        }
        _latest[tenantId] = job;

        _ = Task.Run(async () =>
        {
            string? errorCode;
            using (lease)
                errorCode = await RunAsync(job, export, reader);
            job.End(errorCode);
        }, CancellationToken.None);

        logger.LogInformation("Started Oura import job {JobId}", job.Id);
        return job.GetStatus();
    }

    /// <summary>Refuses a start that <see cref="Start"/> would refuse, before the caller reads the upload.</summary>
    /// <exception cref="OuraImportBlockedException">The tenant's import or a delete of its Oura data is running.</exception>
    public void EnsureCanStart(Guid tenantId)
    {
        if (runGuard.TryGetHolder(tenantId, RunName, out var holder))
            throw new OuraImportBlockedException(byDelete: holder == Guid.Empty);
    }

    /// <summary>The tenant's latest import since the API started, or null.</summary>
    public OuraImportStatus? GetLatest(Guid tenantId) =>
        _latest.TryGetValue(tenantId, out var job) ? job.GetStatus() : null;

    private async Task<string?> RunAsync(OuraImportJob job, byte[] export, TenantReader reader)
    {
        try
        {
            await using var scope = TenantJobScope.OpenFor(services, reader, AuditEndpoint);
            using var zip = new MemoryStream(export, writable: false);
            await scope.ServiceProvider.GetRequiredService<OuraImportService>()
                .ImportAsync(zip, job.Progress, CancellationToken.None);
            return null;
        }
        catch (OuraExportException ex)
        {
            logger.LogWarning("Oura import job {JobId} stopped: {Code}", job.Id, ex.Code);
            return ex.Code;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Oura import job {JobId} failed", job.Id);
            return ImportFailedCode;
        }
    }
}

/// <summary>A start refused because the tenant's import, or a delete of its Oura data, is running.</summary>
/// <remarks>A job holds the run key under its own id; <see cref="OuraImportPurgeGuard"/> holds it under none.</remarks>
public sealed class OuraImportBlockedException(bool byDelete)
    : Exception(byDelete ? "A delete of the Oura data is running." : "An Oura import is already running.")
{
    public bool ByDelete { get; } = byDelete;
}

internal sealed class OuraImportJob(Guid id, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly DateTime _startedAt = time.GetUtcNow().UtcDateTime;
    private OuraImportState _state = OuraImportState.Running;
    private string? _errorCode;
    private DateTime? _completedAt;

    public Guid Id { get; } = id;

    public OuraImportProgress Progress { get; } = new();

    /// <summary>A failure after some records were stored is partial: what was written stays, and a re-import completes it.</summary>
    public void End(string? errorCode)
    {
        lock (_lock)
        {
            _errorCode = errorCode;
            _completedAt = time.GetUtcNow().UtcDateTime;
            _state = errorCode is null ? OuraImportState.Completed
                : Progress.Total > 0 ? OuraImportState.Partial
                : OuraImportState.Failed;
        }
    }

    public OuraImportStatus GetStatus()
    {
        lock (_lock)
        {
            return new OuraImportStatus
            {
                JobId = Id,
                State = _state,
                Phase = _state == OuraImportState.Running ? Progress.Phase : null,
                SleepSessions = Progress.SleepSessionCount,
                HeartRates = Progress.HeartRateCount,
                StepDays = Progress.StepDayCount,
                ErrorCode = _errorCode,
                StartedAt = _startedAt,
                CompletedAt = _completedAt,
            };
        }
    }
}

/// <summary>The records an import has stored so far, written by the job and read by status requests.</summary>
public sealed class OuraImportProgress
{
    private int _phase;
    private int _sleepSessions;
    private int _heartRates;
    private int _stepDays;

    public OuraImportPhase Phase
    {
        get => (OuraImportPhase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }

    public int SleepSessionCount => Volatile.Read(ref _sleepSessions);
    public int HeartRateCount => Volatile.Read(ref _heartRates);
    public int StepDayCount => Volatile.Read(ref _stepDays);
    public int Total => SleepSessionCount + HeartRateCount + StepDayCount;

    public void AddSleepSessions(int count) => Interlocked.Add(ref _sleepSessions, count);
    public void AddHeartRates(int count) => Interlocked.Add(ref _heartRates, count);
    public void AddStepDays(int count) => Interlocked.Add(ref _stepDays, count);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OuraImportState
{
    Running,
    Completed,

    /// <summary>The import stopped after it stored some records. Importing the same export again completes it.</summary>
    Partial,

    /// <summary>The import stopped before it stored any record.</summary>
    Failed,
}

/// <summary>The data set an import is writing; they are written in this order.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OuraImportPhase
{
    Sleep,
    HeartRate,
    Steps,
}

public sealed record OuraImportStatus
{
    public required Guid JobId { get; init; }
    public required OuraImportState State { get; init; }

    /// <summary>Set while the import runs.</summary>
    public OuraImportPhase? Phase { get; init; }

    /// <summary>The records stored so far, or in all once the import has ended.</summary>
    public int SleepSessions { get; init; }

    /// <inheritdoc cref="SleepSessions"/>
    public int HeartRates { get; init; }

    /// <inheritdoc cref="SleepSessions"/>
    public int StepDays { get; init; }

    /// <summary>An <see cref="OuraExportException"/> code, or <see cref="OuraImportJobService.ImportFailedCode"/>, when the import did not complete.</summary>
    public string? ErrorCode { get; init; }

    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

/// <summary>
/// Holds the tenant's import key while its Oura data is purged. Oura has no sync for the persisted
/// active flag to stop, and only an import writes its data.
/// </summary>
public sealed class OuraImportPurgeGuard(TenantRunGuard runGuard, ITenantAccessor tenant) : IConnectorPurgeGuard
{
    public string ConnectorName => OuraConnectorConfiguration.Name;

    public Task<IAsyncDisposable?> PauseSyncsAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(
            runGuard.TryAcquire(tenant.TenantId, OuraImportJobService.RunName) is { } lease ? new Release(lease) : null);

    private sealed class Release(IDisposable lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
