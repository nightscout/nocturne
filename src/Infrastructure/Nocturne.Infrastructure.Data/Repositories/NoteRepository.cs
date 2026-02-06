using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.Interfaces;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Note operations
/// </summary>
public class NoteRepository : INoteRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoteRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public NoteRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<NoteEntity> CreateAsync(NoteEntity entity, CancellationToken cancellationToken = default)
    {
        entity.Id = Guid.CreateVersion7();
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;

        _context.Notes.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return entity;
    }

    /// <inheritdoc />
    public async Task<NoteEntity?> GetByIdAsync(Guid id, bool includeRelated = true, CancellationToken cancellationToken = default)
    {
        var query = _context.Notes.AsQueryable();

        if (includeRelated)
        {
            query = query
                .Include(n => n.ChecklistItems.OrderBy(ci => ci.SortOrder))
                .Include(n => n.Attachments)
                .Include(n => n.TrackerLinks)
                    .ThenInclude(tl => tl.Thresholds)
                .Include(n => n.StateSpanLinks);
        }

        return await query.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<NoteEntity>> GetByUserIdAsync(Guid userId, NoteQueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Notes
            .Include(n => n.ChecklistItems.OrderBy(ci => ci.SortOrder))
            .Include(n => n.Attachments)
            .Include(n => n.TrackerLinks)
                .ThenInclude(tl => tl.Thresholds)
            .Include(n => n.StateSpanLinks)
            .Where(n => n.UserId == userId);

        // Apply optional filters
        if (options != null)
        {
            if (options.Category.HasValue)
            {
                query = query.Where(n => n.Category == options.Category.Value);
            }

            if (options.IsArchived.HasValue)
            {
                query = query.Where(n => n.IsArchived == options.IsArchived.Value);
            }

            if (options.TrackerDefinitionId.HasValue)
            {
                query = query.Where(n => n.TrackerLinks.Any(tl => tl.TrackerDefinitionId == options.TrackerDefinitionId.Value));
            }

            if (options.StateSpanId.HasValue)
            {
                query = query.Where(n => n.StateSpanLinks.Any(sl => sl.StateSpanId == options.StateSpanId.Value));
            }

            if (options.FromDate.HasValue)
            {
                query = query.Where(n => n.OccurredAt >= options.FromDate.Value);
            }

            if (options.ToDate.HasValue)
            {
                query = query.Where(n => n.OccurredAt <= options.ToDate.Value);
            }

            // Apply ordering by OccurredAt descending (most recent first)
            query = query.OrderByDescending(n => n.OccurredAt);

            // Apply pagination
            if (options.Offset.HasValue && options.Offset.Value > 0)
            {
                query = query.Skip(options.Offset.Value);
            }

            if (options.Limit.HasValue && options.Limit.Value > 0)
            {
                query = query.Take(options.Limit.Value);
            }
        }
        else
        {
            // Default ordering by OccurredAt descending
            query = query.OrderByDescending(n => n.OccurredAt);
        }

        return await query.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NoteEntity> UpdateAsync(NoteEntity entity, CancellationToken cancellationToken = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;

        _context.Notes.Update(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return entity;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Notes.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (entity != null)
        {
            _context.Notes.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<NoteChecklistItemEntity?> GetChecklistItemAsync(Guid noteId, Guid itemId, CancellationToken cancellationToken = default)
    {
        return await _context.NoteChecklistItems
            .FirstOrDefaultAsync(ci => ci.NoteId == noteId && ci.Id == itemId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NoteChecklistItemEntity> UpdateChecklistItemAsync(NoteChecklistItemEntity item, CancellationToken cancellationToken = default)
    {
        _context.NoteChecklistItems.Update(item);
        await _context.SaveChangesAsync(cancellationToken);

        return item;
    }

    /// <inheritdoc />
    public async Task<NoteAttachmentEntity> AddAttachmentAsync(NoteAttachmentEntity attachment, CancellationToken cancellationToken = default)
    {
        attachment.Id = Guid.CreateVersion7();
        attachment.CreatedAt = DateTime.UtcNow;

        _context.NoteAttachments.Add(attachment);
        await _context.SaveChangesAsync(cancellationToken);

        return attachment;
    }

    /// <inheritdoc />
    public async Task<NoteAttachmentEntity?> GetAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        return await _context.NoteAttachments
            .FirstOrDefaultAsync(a => a.NoteId == noteId && a.Id == attachmentId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAttachmentAsync(Guid noteId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await _context.NoteAttachments
            .FirstOrDefaultAsync(a => a.NoteId == noteId && a.Id == attachmentId, cancellationToken);

        if (attachment != null)
        {
            _context.NoteAttachments.Remove(attachment);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<NoteTrackerLinkEntity> AddTrackerLinkAsync(NoteTrackerLinkEntity link, CancellationToken cancellationToken = default)
    {
        link.Id = Guid.CreateVersion7();
        link.CreatedAt = DateTime.UtcNow;

        _context.NoteTrackerLinks.Add(link);
        await _context.SaveChangesAsync(cancellationToken);

        return link;
    }

    /// <inheritdoc />
    public async Task DeleteTrackerLinkAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default)
    {
        var link = await _context.NoteTrackerLinks
            .FirstOrDefaultAsync(tl => tl.NoteId == noteId && tl.Id == linkId, cancellationToken);

        if (link != null)
        {
            _context.NoteTrackerLinks.Remove(link);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<NoteStateSpanLinkEntity> AddStateSpanLinkAsync(NoteStateSpanLinkEntity link, CancellationToken cancellationToken = default)
    {
        link.Id = Guid.CreateVersion7();
        link.CreatedAt = DateTime.UtcNow;

        _context.NoteStateSpanLinks.Add(link);
        await _context.SaveChangesAsync(cancellationToken);

        return link;
    }

    /// <inheritdoc />
    public async Task DeleteStateSpanLinkAsync(Guid noteId, Guid linkId, CancellationToken cancellationToken = default)
    {
        var link = await _context.NoteStateSpanLinks
            .FirstOrDefaultAsync(sl => sl.NoteId == noteId && sl.Id == linkId, cancellationToken);

        if (link != null)
        {
            _context.NoteStateSpanLinks.Remove(link);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<NoteEntity>> GetNotesLinkedToTrackerDefinitionAsync(Guid trackerDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _context.Notes
            .Include(n => n.ChecklistItems.OrderBy(ci => ci.SortOrder))
            .Include(n => n.Attachments)
            .Include(n => n.TrackerLinks)
                .ThenInclude(tl => tl.Thresholds)
            .Include(n => n.StateSpanLinks)
            .Where(n => n.TrackerLinks.Any(tl => tl.TrackerDefinitionId == trackerDefinitionId))
            .OrderByDescending(n => n.OccurredAt)
            .ToListAsync(cancellationToken);
    }
}
