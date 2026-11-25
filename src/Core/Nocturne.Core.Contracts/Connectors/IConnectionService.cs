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
    /// <param name="viewerMayKeep">
    /// Whether the caller may remove the expiry of a device credential, as each such credential's
    /// <c>CanKeep</c> says.
    /// </param>
    Task<ConnectionsOverview> GetConnectionsAsync(
        Guid? viewerSubjectId, bool viewerMayKeep, CancellationToken ct = default);

    /// <summary>
    /// Deletes every record the connection of <paramref name="grantIds"/> wrote
    /// (<see cref="IDataSourceService.DeleteWrittenDataAsync"/>), by each credential the list folds into
    /// it, revoked ones and those outside the list's window included. Every one of them has to be an
    /// app or token of <paramref name="viewerSubjectId"/>, and the ids have to name one connection;
    /// otherwise nothing is deleted and the result fails with <see cref="DataSourceDeleteError.NotFound"/>.
    /// </summary>
    Task<DataSourceDeleteResult> DeleteRecordsAsync(
        Guid viewerSubjectId, IReadOnlyCollection<Guid> grantIds, bool allowReimport, CancellationToken ct = default);
}
