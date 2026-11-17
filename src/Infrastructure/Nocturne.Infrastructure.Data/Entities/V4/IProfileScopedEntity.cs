namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// A V4 entity filed under a named profile store: one of the siblings a legacy profile document
/// decomposes into.
/// </summary>
public interface IProfileScopedEntity : IV4TimeSeriesEntity
{
    /// <summary>The profile store the row belongs to.</summary>
    string ProfileName { get; set; }
}
