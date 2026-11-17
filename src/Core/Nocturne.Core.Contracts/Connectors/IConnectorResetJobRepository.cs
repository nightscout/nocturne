using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Stores the records of platform-admin connector cursor reset jobs, so a job's outcome outlives the
/// process that ran it.
/// </summary>
/// <remarks>
/// <c>connector_reset_jobs</c> is operator metadata, not tenant data: it has no tenant policy and is
/// read across tenants on a platform context.
/// </remarks>
public interface IConnectorResetJobRepository
{
    /// <summary>The job's record, or null when none is stored.</summary>
    Task<ConnectorResetJobRecord?> FindAsync(Guid jobId, CancellationToken ct);

    /// <summary>Stores <paramref name="job"/>, replacing the record of the same id.</summary>
    Task SaveAsync(ConnectorResetJobRecord job, CancellationToken ct);
}
