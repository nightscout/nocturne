using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between StateSpan domain models and StateSpanEntity database entities
/// </summary>
public static class StateSpanMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static StateSpanEntity ToEntity(StateSpan stateSpan)
    {
        return new StateSpanEntity
        {
            Id = RecordIdKey.ParseIdToGuid(stateSpan.Id),
            Category = stateSpan.Category.ToString(),
            State = stateSpan.State ?? string.Empty,
            StartTimestamp = stateSpan.StartTimestamp,
            EndTimestamp = stateSpan.EndTimestamp,
            Source = stateSpan.Source,
            MetadataJson = stateSpan.Metadata != null
                ? JsonSerializer.Serialize(stateSpan.Metadata)
                : null,
            OriginalId = stateSpan.OriginalId,
            SupersededById = !string.IsNullOrEmpty(stateSpan.SupersededById)
                ? RecordIdKey.ParseIdToGuid(stateSpan.SupersededById)
                : null,
        };
    }

    /// <summary>
    /// Every stored value <see cref="ParseCategory"/> reads. Rows with any other value are retired and
    /// only ever soft-deleted, so a read that includes deleted rows filters on this
    /// (<see cref="WhereCategoryIsCurrent"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> CategoryNames = Enum.GetNames<StateSpanCategory>();

    /// <summary>
    /// A name lookup rather than <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>, which also
    /// accepts the numeric form and would read a column holding "3" as a real category.
    /// </summary>
    private static readonly Dictionary<string, StateSpanCategory> CategoriesByName =
        Enum.GetValues<StateSpanCategory>().ToDictionary(c => c.ToString(), StringComparer.Ordinal);

    /// <summary>
    /// Whether a stored value is one of <see cref="CategoryNames"/>, and the category it names.
    /// </summary>
    public static bool TryParseCategory(string category, out StateSpanCategory parsed) =>
        CategoriesByName.TryGetValue(category, out parsed);

    /// <summary>
    /// The category a stored value names.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not one of <see cref="CategoryNames"/>.</exception>
    public static StateSpanCategory ParseCategory(string category) =>
        TryParseCategory(category, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Stored state span category '{category}' is not a {nameof(StateSpanCategory)}.");

    /// <summary>
    /// Narrows <paramref name="spans"/> to the rows whose category is one of <see cref="CategoryNames"/>.
    /// </summary>
    public static IQueryable<StateSpanEntity> WhereCategoryIsCurrent(this IQueryable<StateSpanEntity> spans) =>
        spans.Where(s => CategoryNames.Contains(s.Category));

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static StateSpan ToDomainModel(StateSpanEntity entity)
    {
        return new StateSpan
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Category = ParseCategory(entity.Category),
            State = entity.State,
            StartTimestamp = entity.StartTimestamp,
            EndTimestamp = entity.EndTimestamp,
            Source = entity.Source,
            Metadata = MapperHelpers.DeserializeJson<Dictionary<string, object>>(entity.MetadataJson),
            OriginalId = entity.OriginalId,
            SupersededById = entity.SupersededById?.ToString(),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model. A new end from the upload replaces the one
    /// supersession set, so it clears <see cref="StateSpanEntity.SupersededById"/>. An upload with no
    /// end leaves a superseded row closed, since supersession runs only on insert.
    /// </summary>
    public static void UpdateEntity(StateSpanEntity entity, StateSpan stateSpan)
    {
        entity.Category = stateSpan.Category.ToString();
        entity.State = stateSpan.State ?? string.Empty;
        entity.StartTimestamp = stateSpan.StartTimestamp;
        if (stateSpan.EndTimestamp is { } end && end != entity.EndTimestamp)
            entity.SupersededById = null;
        if (entity.SupersededById == null)
            entity.EndTimestamp = stateSpan.EndTimestamp;
        entity.Source = stateSpan.Source;
        entity.MetadataJson = stateSpan.Metadata != null
            ? JsonSerializer.Serialize(stateSpan.Metadata)
            : null;
        entity.OriginalId = stateSpan.OriginalId;
    }
}
