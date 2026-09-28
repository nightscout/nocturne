using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Models;
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
    private readonly PublishSkipTally _skips;

    protected ConnectorPublisherBase(IAuditContext auditContext, PublishSkipTally skips, ILogger logger)
    {
        _auditContext = auditContext ?? throw new ArgumentNullException(nameof(auditContext));
        _skips = skips ?? throw new ArgumentNullException(nameof(skips));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected ILogger Logger { get; }

    /// <summary>
    /// System attribution for a write this base does not itself perform — an upsert loop rather
    /// than a bulk create. Same scope <see cref="PublishAsync"/> writes under, so a connector write
    /// is never attributed to whichever user's request happened to trigger the sync.
    /// </summary>
    protected IDisposable PushSystemAudit() => SystemAuditScope.Push(_auditContext);

    /// <summary>
    /// Records what a write this base does not itself perform left out because the user had deleted
    /// it, as <see cref="PublishAsync"/> does for its own.
    /// </summary>
    protected void RecordSkippedDeleted(int count) => _skips.AddSkippedDeleted(count);

    /// <summary>
    /// <paramref name="beforeWrite"/> runs inside the system audit scope: a preparation step that
    /// writes (an auto-created insulin, a reconcile of the source's window) is attributed to the sync,
    /// and a user-attributed delete would permanently block re-import. <paramref name="afterWrite"/>
    /// runs after a successful write, outside the scope, over the same materialised list.
    /// </summary>
    protected async Task<bool> PublishAsync<TRecord>(
        IEnumerable<TRecord> records,
        IBulkCreateRepository<TRecord> repository,
        string source,
        WriteOrigin origin,
        CancellationToken ct,
        Func<List<TRecord>, Task>? beforeWrite = null,
        Func<List<TRecord>, Task>? afterWrite = null)
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

                var written = await repository.BulkCreateAsync(recordList, origin, ct);
                _skips.AddSkippedDeleted(written.SkippedDeleted);
            }

            if (afterWrite is not null)
                await afterWrite(recordList);

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
    /// Publishes the records of <paramref name="records"/> whose legacy id none of
    /// <paramref name="heldBy"/> holds. Records without an id are dropped: nothing could tell them
    /// apart from what is stored.
    /// </summary>
    /// <param name="heldBy">One lookup per table the record type decomposes into.</param>
    /// <returns>How many were written, or null when a lookup or the write failed.</returns>
    protected async Task<int?> PublishUnheldAsync<TRecord>(
        IEnumerable<TRecord> records,
        Func<TRecord, string?> legacyIdOf,
        Func<List<TRecord>, Task<bool>> publish,
        string source,
        params Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>>[] heldBy)
    {
        List<TRecord> unheld;
        try
        {
            var identified = records.Where(r => legacyIdOf(r) is { Length: > 0 }).ToList();
            var ids = identified.Select(r => legacyIdOf(r)!).ToHashSet(StringComparer.Ordinal);
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lookup in heldBy)
            {
                if (ids.Count > held.Count)
                    held.UnionWith(await lookup(ids));
            }

            unheld = identified.Where(r => !held.Contains(legacyIdOf(r)!)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to compare recent {RecordType} records with what is stored for {Source}",
                typeof(TRecord).Name, source);
            return null;
        }

        if (unheld.Count == 0)
            return 0;

        return await publish(unheld) ? unheld.Count : null;
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
