using Nocturne.Core.Models.Translations;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between TranslationDraft domain models and TranslationDraftEntity database entities.
/// </summary>
public static class TranslationDraftMapper
{
    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static TranslationDraft ToDomainModel(TranslationDraftEntity entity)
    {
        return new TranslationDraft
        {
            Id = entity.Id,
            Locale = entity.Locale,
            Context = entity.Context,
            MsgId = entity.MsgId,
            Translations = entity.Translations,
            UpdatedAt = entity.UpdatedAt,
        };
    }
}
