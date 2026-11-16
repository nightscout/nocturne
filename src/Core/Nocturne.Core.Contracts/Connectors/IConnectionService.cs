using Nocturne.Core.Models.Services;

namespace Nocturne.Core.Contracts.Connectors;

public interface IConnectionService
{
    /// <summary>
    /// The tenant's connections and the records inside the discovery window that none of them wrote.
    /// </summary>
    /// <param name="viewerSubjectId">
    /// The caller, whose unrevoked credentials are listed whether or not they wrote a record. A
    /// credential of another member is listed only when it wrote one.
    /// </param>
    Task<ConnectionsOverview> GetConnectionsAsync(Guid? viewerSubjectId, CancellationToken ct = default);
}
