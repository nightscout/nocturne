using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Runs a tenant-wide connector cursor reset as a background job so the HTTP request that kicks it
/// off can return immediately (202) instead of blocking for the minutes a full-history re-pull can
/// take. Callers poll <see cref="GetStatus"/> for per-connector progress.
/// </summary>
/// <remarks>
/// Modelled on the migration job service: jobs run on detached <see cref="Task"/> instances tracked
/// in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by job id. The fan-out itself lives in
/// the scoped <see cref="IConnectorCursorResetService"/>; this wrapper just owns the lifecycle and
/// translates the engine's <see cref="IConnectorResetProgress"/> callbacks into a pollable snapshot.
/// All endpoints that reach this service are platform-admin only, so jobs are looked up by id alone
/// without per-tenant scoping.
/// </remarks>
/// <seealso cref="IConnectorCursorResetService"/>
public interface IConnectorCursorResetJobService
{
    /// <summary>
    /// Validates the target tenant and enqueues a background reset of every connector it has
    /// configured.
    /// </summary>
    /// <param name="tenantId">The target tenant whose connectors should be re-pulled.</param>
    /// <param name="from">Optional lower bound. When null, all available history is re-ingested.</param>
    /// <param name="dataTypes">Optional data-type filter. When null/empty, every supported type is reset.</param>
    /// <param name="ct">Cancellation token for the validation work (not the background job).</param>
    /// <returns>The created job, or null when the target tenant does not exist.</returns>
    Task<ConnectorResetJobInfo?> StartResetAsync(
        Guid tenantId,
        DateTime? from,
        List<SyncDataType>? dataTypes,
        CancellationToken ct);

    /// <summary>Returns a snapshot of a job's progress.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when no job with that id exists.</exception>
    ConnectorResetJobStatus GetStatus(Guid jobId);

    /// <summary>Requests cancellation of a running job.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when no job with that id exists.</exception>
    void Cancel(Guid jobId);
}

/// <inheritdoc cref="IConnectorCursorResetJobService"/>
public class ConnectorCursorResetJobService : IConnectorCursorResetJobService
{
    private readonly ILogger<ConnectorCursorResetJobService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, ConnectorResetJob> _jobs = new();

    /// <summary>Initializes a new instance of <see cref="ConnectorCursorResetJobService"/>.</summary>
    public ConnectorCursorResetJobService(
        ILogger<ConnectorCursorResetJobService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task<ConnectorResetJobInfo?> StartResetAsync(
        Guid tenantId,
        DateTime? from,
        List<SyncDataType>? dataTypes,
        CancellationToken ct)
    {
        // Validate the tenant and grab its connector list synchronously so we can (a) return 404 for
        // an unknown tenant before enqueuing anything and (b) seed the job's per-connector progress
        // with every connector in "pending" up front, giving the polling UI an immediate, complete
        // list. This short-lived scope is independent of the background task's own scope below.
        TenantConnectorsDto? connectors;
        using (var scope = _serviceProvider.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IConnectorCursorResetService>();
            connectors = await engine.GetTenantConnectorsAsync(tenantId, ct);
        }

        if (connectors is null)
        {
            _logger.LogWarning("Cursor reset job requested for unknown tenant {TenantId}", tenantId);
            return null;
        }

        var jobId = Guid.CreateVersion7();
        var job = new ConnectorResetJob(
            jobId,
            connectors.TenantId,
            connectors.TenantSlug,
            from,
            dataTypes,
            connectors.Connectors.Select(c => c.ConnectorName),
            _logger,
            _serviceProvider);
        _jobs[jobId] = job;

        // Detached background task: deliberately uses CancellationToken.None, not the request token,
        // so the reset outlives the HTTP request that started it. User-initiated cancellation flows
        // through the job's own CancellationTokenSource via Cancel().
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await job.ExecuteAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Connector cursor reset job {JobId} failed", jobId);
                }
            },
            CancellationToken.None);

        _logger.LogInformation(
            "Started connector cursor reset job {JobId} for tenant {TenantSlug} ({ConnectorCount} connectors)",
            jobId, connectors.TenantSlug, connectors.Connectors.Count);

        return job.GetInfo();
    }

    /// <inheritdoc />
    public ConnectorResetJobStatus GetStatus(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
            return job.GetStatus();

        throw new KeyNotFoundException($"Connector cursor reset job {jobId} not found");
    }

    /// <inheritdoc />
    public void Cancel(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Cancel();
            _logger.LogInformation("Cancelled connector cursor reset job {JobId}", jobId);
            return;
        }

        throw new KeyNotFoundException($"Connector cursor reset job {jobId} not found");
    }
}

/// <summary>
/// A single tenant-wide cursor reset running in the background. Tracks lifecycle state and
/// per-connector progress, updated via the <see cref="IConnectorResetProgress"/> callbacks the
/// engine invokes as it fans out.
/// </summary>
internal sealed class ConnectorResetJob : IConnectorResetProgress
{
    private readonly Guid _id;
    private readonly Guid _tenantId;
    private readonly string _tenantSlug;
    private readonly DateTime? _from;
    private readonly List<SyncDataType>? _dataTypes;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTime _createdAt = DateTime.UtcNow;

    // Preserves the configured order from enumeration; updated in place as connectors progress.
    private readonly ConcurrentDictionary<string, ConnectorResetConnectorProgress> _connectors = new();
    private readonly IReadOnlyList<string> _connectorOrder;

    private ConnectorResetJobState _state = ConnectorResetJobState.Pending;
    private string? _errorMessage;
    private DateTime? _startedAt;
    private DateTime? _completedAt;

    public ConnectorResetJob(
        Guid id,
        Guid tenantId,
        string tenantSlug,
        DateTime? from,
        List<SyncDataType>? dataTypes,
        IEnumerable<string> connectorNames,
        ILogger logger,
        IServiceProvider serviceProvider)
    {
        _id = id;
        _tenantId = tenantId;
        _tenantSlug = tenantSlug;
        _from = from;
        _dataTypes = dataTypes;
        _logger = logger;
        _serviceProvider = serviceProvider;

        var order = new List<string>();
        foreach (var name in connectorNames)
        {
            order.Add(name);
            _connectors[name] = new ConnectorResetConnectorProgress
            {
                ConnectorName = name,
                State = ConnectorResetConnectorState.Pending,
            };
        }
        _connectorOrder = order;
    }

    public void Cancel()
    {
        _cts.Cancel();
        // Leave terminal states untouched; otherwise mark cancelled so the snapshot reflects intent
        // even before the background loop observes the token.
        if (_state is ConnectorResetJobState.Pending or ConnectorResetJobState.Running)
            _state = ConnectorResetJobState.Cancelled;
    }

    public async Task ExecuteAsync()
    {
        _startedAt = DateTime.UtcNow;
        _state = ConnectorResetJobState.Running;
        var ct = _cts.Token;

        try
        {
            // Fresh DI scope for the background work — the engine sets the target tenant's context on
            // this scope (DbContext.TenantId, RLS GUC, ITenantAccessor) before fanning out.
            using var scope = _serviceProvider.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IConnectorCursorResetService>();

            var result = await engine.ResetTenantCursorsAsync(_tenantId, _from, _dataTypes, this, ct);

            if (result is null)
            {
                // Tenant vanished between validation and execution (e.g. concurrently deleted).
                _state = ConnectorResetJobState.Failed;
                _errorMessage = "Tenant no longer exists.";
            }
            else
            {
                _state = ConnectorResetJobState.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            _state = ConnectorResetJobState.Cancelled;
        }
        catch (Exception ex)
        {
            _state = ConnectorResetJobState.Failed;
            _errorMessage = ex.Message;
            _logger.LogError(ex, "Connector cursor reset job {JobId} failed", _id);
        }
        finally
        {
            _completedAt = DateTime.UtcNow;
        }
    }

    void IConnectorResetProgress.ConnectorStarted(string connectorName) =>
        _connectors.AddOrUpdate(
            connectorName,
            _ => new ConnectorResetConnectorProgress
            {
                ConnectorName = connectorName,
                State = ConnectorResetConnectorState.Running,
            },
            (_, existing) => existing with { State = ConnectorResetConnectorState.Running });

    void IConnectorResetProgress.ConnectorCompleted(ConnectorCursorResetResult result)
    {
        var state = result.Result.Success
            ? ConnectorResetConnectorState.Succeeded
            : ConnectorResetConnectorState.Failed;

        _connectors[result.ConnectorName] = new ConnectorResetConnectorProgress
        {
            ConnectorName = result.ConnectorName,
            State = state,
            Message = result.Result.Message,
            Result = result.Result,
        };
    }

    public ConnectorResetJobInfo GetInfo() => new()
    {
        JobId = _id,
        TenantId = _tenantId,
        TenantSlug = _tenantSlug,
        CreatedAt = _createdAt,
        State = _state,
        TotalConnectors = _connectorOrder.Count,
    };

    public ConnectorResetJobStatus GetStatus()
    {
        var connectors = OrderedConnectors();
        return new ConnectorResetJobStatus
        {
            JobId = _id,
            TenantId = _tenantId,
            TenantSlug = _tenantSlug,
            State = _state,
            CreatedAt = _createdAt,
            StartedAt = _startedAt,
            CompletedAt = _completedAt,
            ErrorMessage = _errorMessage,
            TotalConnectors = connectors.Count,
            CompletedConnectors = connectors.Count(c =>
                c.State is ConnectorResetConnectorState.Succeeded or ConnectorResetConnectorState.Failed),
            Connectors = connectors,
        };
    }

    /// <summary>Snapshots connectors in their original configured order, appending any later arrivals.</summary>
    private IReadOnlyList<ConnectorResetConnectorProgress> OrderedConnectors()
    {
        var ordered = new List<ConnectorResetConnectorProgress>(_connectors.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in _connectorOrder)
        {
            if (_connectors.TryGetValue(name, out var p) && seen.Add(name))
                ordered.Add(p);
        }

        foreach (var kvp in _connectors)
        {
            if (seen.Add(kvp.Key))
                ordered.Add(kvp.Value);
        }

        return ordered;
    }
}

/// <summary>Lifecycle state of a connector cursor reset job.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorResetJobState
{
    /// <summary>Created and queued, not yet started.</summary>
    Pending,
    /// <summary>Actively re-pulling connectors.</summary>
    Running,
    /// <summary>Every connector has been processed (individual connectors may still have failed).</summary>
    Completed,
    /// <summary>The job terminated due to an unrecoverable error before completing.</summary>
    Failed,
    /// <summary>The job was cancelled before completing.</summary>
    Cancelled,
}

/// <summary>State of a single connector within a reset job.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorResetConnectorState
{
    /// <summary>Queued, not yet started.</summary>
    Pending,
    /// <summary>Currently re-pulling.</summary>
    Running,
    /// <summary>Re-pull completed successfully.</summary>
    Succeeded,
    /// <summary>Re-pull failed.</summary>
    Failed,
}

/// <summary>Progress for a single connector within a reset job.</summary>
public record ConnectorResetConnectorProgress
{
    /// <summary>The connector name (e.g. <c>nightscout</c>).</summary>
    public required string ConnectorName { get; init; }

    /// <summary>The connector's current state in this job.</summary>
    public ConnectorResetConnectorState State { get; init; }

    /// <summary>A human-readable message for the outcome, once the connector has completed.</summary>
    public string? Message { get; init; }

    /// <summary>The full sync result, once the connector has completed.</summary>
    public SyncResult? Result { get; init; }
}

/// <summary>Summary returned when a reset job is created (202 Accepted).</summary>
public record ConnectorResetJobInfo
{
    /// <summary>The job id, used to poll status.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The target tenant being reset.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The target tenant's slug, for display.</summary>
    public required string TenantSlug { get; init; }

    /// <summary>When the job was created.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>The job's current state.</summary>
    public ConnectorResetJobState State { get; init; }

    /// <summary>How many connectors the job will reset.</summary>
    public int TotalConnectors { get; init; }
}

/// <summary>A pollable snapshot of a reset job's progress.</summary>
public record ConnectorResetJobStatus
{
    /// <summary>The job id.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The target tenant being reset.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The target tenant's slug, for display.</summary>
    public required string TenantSlug { get; init; }

    /// <summary>The job's current lifecycle state.</summary>
    public required ConnectorResetJobState State { get; init; }

    /// <summary>When the job was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When the background work started, or null if not yet started.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When the job reached a terminal state, or null if still running.</summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>An error message when the whole job failed (not a single connector).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Total connectors the job will reset.</summary>
    public int TotalConnectors { get; init; }

    /// <summary>How many connectors have finished (succeeded or failed).</summary>
    public int CompletedConnectors { get; init; }

    /// <summary>Per-connector progress, in configured order.</summary>
    public IReadOnlyList<ConnectorResetConnectorProgress> Connectors { get; init; } = [];
}
