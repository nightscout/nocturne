using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Filters;

/// <summary>
/// Writes <see cref="ReadAccessAuditFilter"/>'s entries after the request has moved on.
/// </summary>
/// <remarks>
/// A singleton, so its factory is the root one and reads no request's scope: the write may run after
/// the request's scope is disposed, and while the pipeline is still changing that scope's reader
/// state. The tenant comes from the entry, captured as a value.
/// </remarks>
public sealed class ReadAccessLogWriter(
    INocturneDbContextFactory contexts,
    ILogger<ReadAccessLogWriter> logger)
{
    /// <summary>Starts the write and returns without waiting for it; a failure is logged.</summary>
    public void Enqueue(ReadAccessLogEntity entry) => _ = WriteAsync(entry);

    /// <summary>Writes <paramref name="entry"/> on a context pinned to its tenant.</summary>
    internal async Task WriteAsync(ReadAccessLogEntity entry)
    {
        try
        {
            await using var ctx = await contexts.CreateForTenantAsync(entry.TenantId);
            ctx.ReadAccessLog.Add(entry);
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write read access audit log entry for {Endpoint}", entry.Endpoint);
        }
    }
}
