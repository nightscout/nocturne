using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing <see cref="BasalInjection"/> records (discrete long-acting basal
/// insulin injections, MDI). Soft-deletes via <c>DeletedAt</c>; the global query filter
/// configured in <see cref="NocturneDbContext"/> excludes soft-deleted rows from reads.
/// </summary>
public class BasalInjectionRepository : IBasalInjectionRepository
{
    private readonly NocturneDbContext _context;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<BasalInjectionRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasalInjectionRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public BasalInjectionRepository(
        NocturneDbContext context,
        IAuditContext auditContext,
        ILogger<BasalInjectionRepository> logger)
    {
        _context = context;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets basal injection records based on filter criteria.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of basal injection records.</returns>
    public async Task<IEnumerable<BasalInjection>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    )
    {
        var query = _context.BasalInjections.AsNoTracking().AsQueryable();
        if (from is { } fromValue)
            query = query.Where(e => e.Timestamp >= fromValue);
        if (to is { } toValue)
            query = query.Where(e => e.Timestamp <= toValue);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a basal injection record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The basal injection record, or null if not found.</returns>
    public async Task<BasalInjection?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Use FirstOrDefaultAsync instead of FindAsync so the soft-delete global query
        // filter (WHERE DeletedAt IS NULL) is always applied. FindAsync checks the change
        // tracker first and can return a cached soft-deleted entity.
        var entity = await _context.BasalInjections
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        return entity is null ? null : BasalInjectionMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new basal injection record. When <c>DataSource</c> and <c>SyncIdentifier</c>
    /// match an existing row for this tenant, the record is updated in place (upsert) rather
    /// than inserted — making the operation idempotent for connector replays.
    /// Tenant scoping is implicit via the DbContext's RLS-equivalent query filter.
    /// </summary>
    /// <remarks>
    /// The controller layer has its own idempotency check that returns the existing record
    /// unchanged (HTTP semantics). This repository-level upsert exists for non-HTTP callers
    /// (connectors, background services) that need "latest wins" semantics on replay.
    /// </remarks>
    /// <param name="model">The basal injection to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created or updated basal injection record.</returns>
    public async Task<BasalInjection> CreateAsync(BasalInjection model, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(model.DataSource) && !string.IsNullOrEmpty(model.SyncIdentifier))
        {
            var existing = await _context.BasalInjections
                .FirstOrDefaultAsync(
                    e => e.DataSource == model.DataSource && e.SyncIdentifier == model.SyncIdentifier,
                    ct);
            if (existing != null)
            {
                BasalInjectionMapper.UpdateEntity(existing, model);
                await _context.SaveChangesAsync(ct);
                return BasalInjectionMapper.ToDomainModel(existing);
            }
        }

        var entity = BasalInjectionMapper.ToEntity(model);
        _context.BasalInjections.Add(entity);
        await _context.SaveChangesAsync(ct);
        return BasalInjectionMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing basal injection record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated basal injection record.</returns>
    public async Task<BasalInjection> UpdateAsync(Guid id, BasalInjection model, CancellationToken ct = default)
    {
        var entity =
            await _context.BasalInjections.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new KeyNotFoundException($"BasalInjection {id} not found");
        BasalInjectionMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return BasalInjectionMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Soft-deletes a basal injection record by setting <c>DeletedAt</c>. The row remains
    /// in the database but is excluded from reads by the global query filter.
    /// The <c>MutationAuditInterceptor</c> writes a "delete" audit entry automatically when
    /// it observes the null → non-null transition on <c>DeletedAt</c>.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.BasalInjections.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new KeyNotFoundException($"BasalInjection {id} not found");
        entity.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<BasalInjection> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted BasalInjection {id} not found");
        entity.DeletedAt = null;
        await _context.SaveChangesAsync(ct);
        return BasalInjectionMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BasalInjection>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        var entities = await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await _context.SaveChangesAsync(ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BasalInjection>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        var entities = await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(BasalInjectionMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        return await _context.BasalInjections.IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Counts basal injection records within a timestamp range.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    public async Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.BasalInjections.AsNoTracking().AsQueryable();
        if (from is { } fromValue)
            query = query.Where(e => e.Timestamp >= fromValue);
        if (to is { } toValue)
            query = query.Where(e => e.Timestamp <= toValue);
        return await query.CountAsync(ct);
    }

    /// <summary>
    /// Soft-deletes basal injection records matching the given data source and sync identifier
    /// by setting <c>DeletedAt</c> on each row. The global query filter hides soft-deleted rows
    /// from subsequent reads; the <c>MutationAuditInterceptor</c> writes per-row audit entries
    /// when it observes the null → non-null transition on <c>DeletedAt</c>.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of soft-deleted records.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        var entities = await _context.BasalInjections
            .Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier)
            .ToListAsync(ct);

        if (entities.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var entity in entities)
            entity.DeletedAt = now;

        await _context.SaveChangesAsync(ct);
        return entities.Count;
    }

    /// <summary>
    /// Finds a single basal injection by data source and sync identifier. The global query
    /// filter automatically scopes the lookup to the current tenant and excludes soft-deleted rows.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    public async Task<BasalInjection?> FindBySyncIdentifierAsync(string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        var entity = await _context.BasalInjections
            .FirstOrDefaultAsync(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier, ct);
        return entity is null ? null : BasalInjectionMapper.ToDomainModel(entity);
    }
}
