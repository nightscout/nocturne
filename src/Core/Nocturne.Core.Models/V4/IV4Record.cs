namespace Nocturne.Core.Models.V4;

/// <summary>
/// Shared interface for all V4 domain models, providing common traceability and metadata properties.
/// </summary>
public interface IV4Record
{
    Guid Id { get; set; }
    long Mills { get; set; }
    int? UtcOffset { get; set; }
    string? Device { get; set; }
    string? App { get; set; }
    string? DataSource { get; set; }
    Guid? CorrelationId { get; set; }
    string? LegacyId { get; set; }
    DateTime CreatedAt { get; set; }
    DateTime ModifiedAt { get; set; }
}
