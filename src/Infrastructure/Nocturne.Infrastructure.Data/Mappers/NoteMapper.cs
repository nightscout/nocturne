using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Note domain models and NoteEntity database entities
/// </summary>
public static class NoteMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static NoteEntity ToEntity(Note model)
    {
        var entity = new NoteEntity
        {
            Id = string.IsNullOrEmpty(model.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(model.Id),
            UserId = model.UserId,
            Category = model.Category,
            Title = model.Title,
            Content = model.Content,
            OccurredAt = model.OccurredAt ?? DateTime.UtcNow,
            IsArchived = model.IsArchived,
            CreatedAt = model.CreatedAt == default ? DateTime.UtcNow : model.CreatedAt,
            UpdatedAt = model.UpdatedAt == default ? DateTime.UtcNow : model.UpdatedAt,
        };

        // Map checklist items
        foreach (var item in model.ChecklistItems)
        {
            entity.ChecklistItems.Add(ToChecklistItemEntity(item, entity.Id));
        }

        // Note: Attachments should be added separately via AddAttachmentAsync
        // TrackerLinks and StateSpanLinks are also managed separately

        return entity;
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Note ToModel(NoteEntity entity)
    {
        var model = new Note
        {
            Id = entity.Id.ToString(),
            UserId = entity.UserId,
            Category = entity.Category,
            Title = entity.Title,
            Content = entity.Content,
            OccurredAt = entity.OccurredAt,
            IsArchived = entity.IsArchived,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };

        // Map checklist items
        model.ChecklistItems = entity.ChecklistItems
            .OrderBy(ci => ci.SortOrder)
            .Select(ToChecklistItemModel)
            .ToList();

        // Map tracker links with thresholds
        model.TrackerLinks = entity.TrackerLinks
            .Select(ToTrackerLinkModel)
            .ToList();

        // Map state span links
        model.StateSpanLinks = entity.StateSpanLinks
            .Select(ToStateSpanLinkModel)
            .ToList();

        return model;
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// Preserves Id, CreatedAt
    /// </summary>
    public static void UpdateEntity(NoteEntity entity, Note model)
    {
        entity.UserId = model.UserId;
        entity.Category = model.Category;
        entity.Title = model.Title;
        entity.Content = model.Content;
        entity.OccurredAt = model.OccurredAt ?? entity.OccurredAt;
        entity.IsArchived = model.IsArchived;
        entity.UpdatedAt = DateTime.UtcNow;

        // Update existing checklist items and add new ones
        var existingItemIds = entity.ChecklistItems.Select(ci => ci.Id).ToHashSet();
        var modelItemIds = model.ChecklistItems
            .Where(ci => !string.IsNullOrEmpty(ci.Id))
            .Select(ci => ParseIdToGuid(ci.Id!))
            .ToHashSet();

        // Remove items not in model
        var itemsToRemove = entity.ChecklistItems
            .Where(ci => !modelItemIds.Contains(ci.Id))
            .ToList();
        foreach (var item in itemsToRemove)
        {
            entity.ChecklistItems.Remove(item);
        }

        // Update existing and add new items
        foreach (var modelItem in model.ChecklistItems)
        {
            var itemId = string.IsNullOrEmpty(modelItem.Id)
                ? Guid.Empty
                : ParseIdToGuid(modelItem.Id);

            var existingItem = entity.ChecklistItems.FirstOrDefault(ci => ci.Id == itemId);
            if (existingItem != null)
            {
                existingItem.Text = modelItem.Text;
                existingItem.IsCompleted = modelItem.IsCompleted;
                existingItem.CompletedAt = modelItem.CompletedAt;
                existingItem.SortOrder = modelItem.SortOrder;
            }
            else
            {
                entity.ChecklistItems.Add(ToChecklistItemEntity(modelItem, entity.Id));
            }
        }
    }

    /// <summary>
    /// Convert checklist item model to entity
    /// </summary>
    public static NoteChecklistItemEntity ToChecklistItemEntity(NoteChecklistItem model, Guid noteId)
    {
        return new NoteChecklistItemEntity
        {
            Id = string.IsNullOrEmpty(model.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(model.Id),
            NoteId = noteId,
            Text = model.Text,
            IsCompleted = model.IsCompleted,
            CompletedAt = model.CompletedAt,
            SortOrder = model.SortOrder,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert checklist item entity to model
    /// </summary>
    public static NoteChecklistItem ToChecklistItemModel(NoteChecklistItemEntity entity)
    {
        return new NoteChecklistItem
        {
            Id = entity.Id.ToString(),
            Text = entity.Text,
            IsCompleted = entity.IsCompleted,
            CompletedAt = entity.CompletedAt,
            SortOrder = entity.SortOrder,
        };
    }

    /// <summary>
    /// Convert attachment entity to model (metadata only, data handled separately)
    /// </summary>
    public static NoteAttachment ToAttachmentModel(NoteAttachmentEntity entity)
    {
        return new NoteAttachment
        {
            Id = entity.Id.ToString(),
            FileName = entity.FileName,
            MimeType = entity.MimeType,
            SizeBytes = entity.SizeBytes,
            CreatedAt = entity.CreatedAt,
        };
    }

    /// <summary>
    /// Convert tracker link entity to model with thresholds
    /// </summary>
    public static NoteTrackerLink ToTrackerLinkModel(NoteTrackerLinkEntity entity)
    {
        return new NoteTrackerLink
        {
            Id = entity.Id.ToString(),
            TrackerDefinitionId = entity.TrackerDefinitionId,
            CreatedAt = entity.CreatedAt,
            Thresholds = entity.Thresholds
                .Select(ToThresholdModel)
                .ToList(),
        };
    }

    /// <summary>
    /// Convert threshold entity to model
    /// </summary>
    public static NoteTrackerThreshold ToThresholdModel(NoteTrackerThresholdEntity entity)
    {
        return new NoteTrackerThreshold
        {
            Id = entity.Id.ToString(),
            HoursOffset = entity.HoursOffset,
            Urgency = entity.Urgency,
            Description = entity.Description,
        };
    }

    /// <summary>
    /// Convert threshold model to entity
    /// </summary>
    public static NoteTrackerThresholdEntity ToThresholdEntity(NoteTrackerThreshold model, Guid linkId)
    {
        return new NoteTrackerThresholdEntity
        {
            Id = string.IsNullOrEmpty(model.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(model.Id),
            NoteTrackerLinkId = linkId,
            HoursOffset = model.HoursOffset,
            Urgency = model.Urgency,
            Description = model.Description,
        };
    }

    /// <summary>
    /// Convert state span link entity to model
    /// </summary>
    public static NoteStateSpanLink ToStateSpanLinkModel(NoteStateSpanLinkEntity entity)
    {
        return new NoteStateSpanLink
        {
            Id = entity.Id.ToString(),
            StateSpanId = entity.StateSpanId.ToString(),
            CreatedAt = entity.CreatedAt,
        };
    }

    /// <summary>
    /// Parse string ID to GUID, or generate new GUID if invalid
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var guidId))
            return guidId;

        // Hash the ID to get a deterministic GUID for legacy IDs
        try
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
            var guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);
            return new Guid(guidBytes);
        }
        catch
        {
            return Guid.CreateVersion7();
        }
    }
}
