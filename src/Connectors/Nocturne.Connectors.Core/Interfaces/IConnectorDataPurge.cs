using Nocturne.Core.Contracts.Audit;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Describes and removes the records a connector wrote that carry no data source id, which the
///     source purge and the source filters of discovery and the summary cannot reach.
/// </summary>
public interface IConnectorDataPurge
{
    /// <summary>The connector name the purge covers, matched without regard to case.</summary>
    string ConnectorName { get; }

    /// <summary>
    ///     Counts the current tenant's records the purge would delete, by data type: those from
    ///     <paramref name="since"/> on, or all of them when it is null.
    /// </summary>
    Task<IReadOnlyList<ConnectorRecordStats>> DescribeAsync(
        DateTime? since, DateTime last24Hours, CancellationToken ct);

    /// <summary>
    ///     Deletes the current tenant's records as the source purge does: attributed to
    ///     <paramref name="auditContext"/>, so no sync re-imports them, unless
    ///     <paramref name="allowReimport"/> releases them and resets the connector's sync position.
    ///     Returns the deleted counts by data type.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> PurgeAsync(
        bool allowReimport, IAuditContext auditContext, CancellationToken ct);
}

/// <summary>One data type's records of a connector, as counted by <see cref="IConnectorDataPurge.DescribeAsync"/>.</summary>
public sealed record ConnectorRecordStats(
    string DataType, long Count, int CountLast24Hours, DateTime Latest, DateTime? Oldest);
