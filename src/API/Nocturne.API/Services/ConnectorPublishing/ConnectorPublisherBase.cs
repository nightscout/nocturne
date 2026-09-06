using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// The write shape every connector publisher shares: materialise the batch, skip an empty one,
/// bulk-create it under system audit attribution, and report a failure as <c>false</c> instead of
/// propagating it into the connector's sync loop.
/// </summary>
internal abstract class ConnectorPublisherBase
{
    private readonly IAuditContext _auditContext;

    protected ConnectorPublisherBase(IAuditContext auditContext, ILogger logger)
    {
        _auditContext = auditContext ?? throw new ArgumentNullException(nameof(auditContext));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected ILogger Logger { get; }

    /// <summary>
    /// <paramref name="beforeWrite"/> runs inside the system audit scope: a preparation step that
    /// writes (an auto-created insulin, a reconcile of the source's window) is attributed to the sync,
    /// and a user-attributed delete would permanently block re-import. <paramref name="afterWrite"/>
    /// runs after a successful write, outside the scope.
    /// </summary>
    protected async Task<bool> PublishAsync<TRecord>(
        IEnumerable<TRecord> records,
        IBulkCreateRepository<TRecord> repository,
        string source,
        WriteOrigin origin,
        CancellationToken ct,
        Func<List<TRecord>, Task>? beforeWrite = null,
        Func<Task>? afterWrite = null)
    {
        var recordType = typeof(TRecord).Name;
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            using (SystemAuditScope.Push(_auditContext))
            {
                if (beforeWrite is not null)
                    await beforeWrite(recordList);

                await repository.BulkCreateAsync(recordList, origin, ct);
            }

            if (afterWrite is not null)
                await afterWrite();

            Logger.LogDebug(
                "Published {Count} {RecordType} records for {Source}", recordList.Count, recordType, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to publish {RecordType} records for {Source}", recordType, source);
            return false;
        }
    }

    /// <summary>
    /// The resume watermark for a connector sync whose legacy collection spans several stored record
    /// types: the latest timestamp any of them holds for THIS source. Source-scoping is required for
    /// multi-connector catch-up — a tenant-global latest mis-classifies a newly enabled connector's
    /// first sync as incremental and skips its backfill.
    /// </summary>
    protected static async Task<DateTime?> LatestTimestampAsync(params Func<Task<DateTime?>>[] perType)
    {
        DateTime? latest = null;
        foreach (var read in perType)
        {
            if (await read() is { } candidate && (latest is null || candidate > latest))
                latest = candidate;
        }

        return latest;
    }
}
