using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class NoteRepository : INoteRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<NoteRepository> _logger;

    public NoteRepository(NocturneDbContext context, ILogger<NoteRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<Note>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.Notes.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(NoteMapper.ToDomainModel);
    }

    public async Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Notes.FindAsync([id], ct);
        return entity is null ? null : NoteMapper.ToDomainModel(entity);
    }

    public async Task<Note?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.Notes.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : NoteMapper.ToDomainModel(entity);
    }

    public async Task<Note> CreateAsync(Note model, CancellationToken ct = default)
    {
        var entity = NoteMapper.ToEntity(model);
        _context.Notes.Add(entity);
        await _context.SaveChangesAsync(ct);
        return NoteMapper.ToDomainModel(entity);
    }

    public async Task<Note> UpdateAsync(Guid id, Note model, CancellationToken ct = default)
    {
        var entity = await _context.Notes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Note {id} not found");
        NoteMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return NoteMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Notes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Note {id} not found");
        _context.Notes.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.Notes.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<Note>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.Notes
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(NoteMapper.ToDomainModel);
    }

    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.Notes
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }
}
