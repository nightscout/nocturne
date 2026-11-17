using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Filters;

/// <summary>
/// Queues <see cref="ReadAccessAuditFilter"/>'s entries and writes them in batches after the request
/// has moved on.
/// </summary>
/// <remarks>
/// A singleton, so its factory is the root one and reads no request's scope: the write runs after
/// the request's scope is disposed, and while the pipeline is still changing that scope's reader
/// state. The tenant comes from the entry, captured as a value. <see cref="ReadAccessLogDrain"/>
/// runs <see cref="DrainAsync"/>; the queue is bounded so a stalled database costs audit rows, never
/// request latency. A free-text column too long for its column is cut on save
/// (<see cref="FitToColumnAttribute"/>), so one long value cannot fail the batch it shares.
/// </remarks>
public sealed class ReadAccessLogWriter(
    INocturneDbContextFactory contexts,
    TimeProvider clock,
    ILogger<ReadAccessLogWriter> logger)
{
    /// <summary>
    /// An entry is about 1 KB, so a full queue holds about 10 MB. <c>UserAgent</c> and the query
    /// parameter keys are client-chosen and uncapped, so a crafted stream of requests can make it larger.
    /// </summary>
    internal const int Capacity = 10_000;

    /// <summary>One batch is one round trip per tenant, and bounds how long a single save holds a connection.</summary>
    internal const int BatchSize = 200;

    /// <summary>A sustained overflow logs once a minute, not once per dropped entry.</summary>
    internal static readonly TimeSpan DropLogInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<ReadAccessLogEntity> queue = Channel.CreateBounded<ReadAccessLogEntity>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private long dropped;
    private long lastDropReport;

    /// <summary>
    /// Queues <paramref name="entry"/> and returns at once. A full queue drops the entry rather
    /// than make the request wait.
    /// </summary>
    public void Enqueue(ReadAccessLogEntity entry)
    {
        if (queue.Writer.TryWrite(entry))
            return;

        Interlocked.Increment(ref dropped);
        ReportDrops(force: false);
    }

    /// <summary>Counts and logs the entries still queued, for a stop whose time ran out before they were written.</summary>
    internal void ReportDiscarded()
    {
        var count = queue.Reader.Count;
        if (count > 0)
        {
            logger.LogWarning(
                "Discarded {Count} queued read access audit log entries because the host stopped before they were written",
                count);
        }
    }

    /// <summary>Stops accepting entries; <see cref="DrainAsync"/> returns once the queue is empty.</summary>
    internal void Complete() => queue.Writer.TryComplete();

    /// <summary>Writes queued entries, a batch at a time, until the queue is completed and empty.</summary>
    internal async Task DrainAsync()
    {
        var batch = new List<ReadAccessLogEntity>(BatchSize);
        while (await queue.Reader.WaitToReadAsync())
        {
            while (batch.Count < BatchSize && queue.Reader.TryRead(out var entry))
                batch.Add(entry);

            await WriteBatchAsync(batch);
            batch.Clear();
            ReportDrops(force: false);
        }

        ReportDrops(force: true);
    }

    /// <summary>
    /// Writes each tenant's entries on a context pinned to that tenant. A group the database refuses
    /// for its data is retried one entry at a time, so only the entries it refuses are lost. Any other
    /// failure loses the group, since a retry would meet the same fault once per entry. Losses are
    /// counted and logged, and never stop the other groups.
    /// </summary>
    internal async Task WriteBatchAsync(IReadOnlyCollection<ReadAccessLogEntity> batch)
    {
        foreach (var group in batch.GroupBy(e => e.TenantId))
        {
            var entries = group.ToList();
            var failure = await TrySaveAsync(group.Key, entries);
            if (failure is null)
                continue;

            var lost = entries.Count;
            if (entries.Count > 1 && failure is DbUpdateException update && update.IsDataError())
            {
                lost = 0;
                foreach (var entry in entries)
                {
                    if (await TrySaveAsync(group.Key, [entry]) is { } entryFailure)
                    {
                        lost++;
                        failure = entryFailure;
                    }
                }
            }

            if (lost > 0)
            {
                logger.LogWarning(
                    failure,
                    "Failed to write {Lost} of {Count} read access audit log entries for tenant {TenantId}",
                    lost,
                    entries.Count,
                    group.Key);
            }
        }
    }

    /// <summary>
    /// Nothing a save throws may end <see cref="DrainAsync"/>: a drain that stops loses every later
    /// entry until the process restarts. No token reaches the save, so a cancellation here is not the
    /// drain being stopped.
    /// </summary>
    private async Task<Exception?> TrySaveAsync(Guid tenantId, IReadOnlyCollection<ReadAccessLogEntity> entries)
    {
        try
        {
            await using var ctx = await contexts.CreateForTenantAsync(tenantId);
            ctx.ReadAccessLog.AddRange(entries);
            await ctx.SaveChangesAsync();
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex;
        }
    }

    private void ReportDrops(bool force)
    {
        if (Volatile.Read(ref dropped) == 0)
            return;

        var now = clock.GetTimestamp();
        var last = Volatile.Read(ref lastDropReport);
        if (!force && last != 0 && clock.GetElapsedTime(last, now) < DropLogInterval)
            return;

        if (Interlocked.CompareExchange(ref lastDropReport, now, last) != last)
            return;

        var count = Interlocked.Exchange(ref dropped, 0);
        if (count > 0)
        {
            logger.LogWarning(
                "Dropped {Count} read access audit log entries because the write queue was full",
                count);
        }
    }
}
