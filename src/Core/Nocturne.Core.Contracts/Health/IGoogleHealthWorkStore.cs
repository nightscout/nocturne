using Nocturne.Core.Models.Health;

namespace Nocturne.Core.Contracts.Health;

/// <summary>
///     How far the tenant's current Google Health import has got. A <see cref="WorkerOwned"/> one is
///     a queued import the worker runs, which a restart resumes.
/// </summary>
public sealed record GoogleHealthSyncProgress(
    GoogleHealthSyncPhase Phase,
    string? DataType,
    int CompletedDataTypes,
    int TotalDataTypes,
    int PagesRead,
    bool WorkerOwned = false);

/// <summary>A disconnect the tenant asked for that has not finished, which a restart resumes.</summary>
public sealed record GoogleHealthDisconnectRequest(Guid RequestId, Guid SubjectId);

/// <summary>The tenant's Google Health work in flight; either part is null when there is none.</summary>
public sealed record GoogleHealthWork(GoogleHealthSyncProgress? Progress, GoogleHealthDisconnectRequest? Disconnect)
{
    public static readonly GoogleHealthWork None = new(null, null);
}

/// <summary>
///     The current tenant's Google Health work in flight (<see cref="GoogleHealthWork"/>), kept apart
///     from the connector's own state so that a purge or a cursor reset of that state never drops a
///     queued import or a disconnect half done.
/// </summary>
public interface IGoogleHealthWorkStore
{
    /// <summary>The current tenant's work; <see cref="GoogleHealthWork.None"/> when it has none.</summary>
    Task<GoogleHealthWork> GetAsync(CancellationToken ct);

    /// <summary>Replaces the progress, or clears it when <paramref name="progress"/> is null, leaving the disconnect.</summary>
    Task SetProgressAsync(GoogleHealthSyncProgress? progress, CancellationToken ct);

    /// <summary>Replaces the disconnect, or clears it when <paramref name="request"/> is null, leaving the progress.</summary>
    Task SetDisconnectAsync(GoogleHealthDisconnectRequest? request, CancellationToken ct);
}
