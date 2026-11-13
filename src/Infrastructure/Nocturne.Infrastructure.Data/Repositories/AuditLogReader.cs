using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <inheritdoc cref="IAuditLogReader"/>
/// <remarks>
/// The scope's reader opens each context. Neither table has a governing share scope and neither
/// is clamped (<c>ShareDataCategoriesGuardTests</c>): a public share sees none of their rows, and
/// a history-clamped member sees full history. The two logs name the shared columns alike, so one
/// query shape serves both and a log's own filter is the only part that differs.
/// </remarks>
public class AuditLogReader(INocturneDbContextFactory contexts) : IAuditLogReader
{
    public async Task<PaginatedResponse<MutationAuditEntry>> GetMutationsAsync(
        AuditLogWindow window, MutationAuditFilter filter, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = Narrow(db.MutationAuditLog.AsNoTracking(), window);

        if (!string.IsNullOrEmpty(filter.Action))
            rows = rows.Where(e => e.Action == filter.Action);
        if (filter.EntityId.HasValue)
            rows = rows.Where(e => e.EntityId == filter.EntityId.Value);

        return await PageAsync(rows, window, e => new MutationAuditEntry(
            e.Id, e.CreatedAt, e.EntityType, e.EntityId, e.Action, e.ChangesJson, e.ActorSubjectId,
            e.ActorName, e.AuthType, e.IpAddress, e.Endpoint), ct);
    }

    public async Task<PaginatedResponse<ReadAccessAuditEntry>> GetReadsAsync(
        AuditLogWindow window, ReadAccessLogFilter filter, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var rows = Narrow(db.ReadAccessLog.AsNoTracking(), window);

        if (!string.IsNullOrEmpty(filter.Endpoint))
            rows = rows.Where(e => e.Endpoint == filter.Endpoint);
        if (filter.StatusCode.HasValue)
            rows = rows.Where(e => e.StatusCode == filter.StatusCode.Value);

        return await PageAsync(rows, window, e => new ReadAccessAuditEntry(
            e.Id, e.CreatedAt, e.Endpoint, e.EntityType, e.RecordCount, e.StatusCode, e.QueryParametersJson,
            e.ActorSubjectId, e.ActorName, e.AuthType, e.IpAddress, e.CredentialFingerprint), ct);
    }

    private static IQueryable<T> Narrow<T>(IQueryable<T> rows, AuditLogWindow window) where T : class, IAuditLogRow
    {
        rows = rows.Where(e => e.CreatedAt >= window.From
            && e.CreatedAt <= window.To);

        if (window.ActorSubjectId.HasValue)
            rows = rows.Where(e => e.ActorSubjectId == window.ActorSubjectId.Value);
        if (!string.IsNullOrEmpty(window.EntityType))
            rows = rows.Where(e => e.EntityType == window.EntityType);

        return rows;
    }

    private static async Task<PaginatedResponse<TOut>> PageAsync<T, TOut>(
        IQueryable<T> rows,
        AuditLogWindow window,
        Expression<Func<T, TOut>> project,
        CancellationToken ct) where T : class, IAuditLogRow
    {
        var total = await rows.CountAsync(ct);

        var ordered = window.OldestFirst
            ? rows.OrderBy(e => e.CreatedAt)
            : rows.OrderByDescending(e => e.CreatedAt);

        var items = await ordered
            .Skip(window.Offset)
            .Take(window.Limit)
            .Select(project)
            .ToListAsync(ct);

        return new PaginatedResponse<TOut>
        {
            Data = items,
            Pagination = new PaginationInfo(window.Limit, window.Offset, total),
        };
    }
}
