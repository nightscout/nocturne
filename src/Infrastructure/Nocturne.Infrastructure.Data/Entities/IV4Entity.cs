namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for V4 clinical entities that participate in tenant-scoped soft-delete
/// dedup. Implementations expose <see cref="Id"/>, <see cref="LegacyId"/>,
/// <see cref="CorrelationId"/>, <see cref="ITenantScoped.TenantId"/>, and
/// <see cref="ISoftDeletable.DeletedAt"/>.
/// </summary>
public interface IV4Entity : ITenantScoped, ISoftDeletable
{
    Guid Id { get; set; }
    string? LegacyId { get; set; }

    /// <summary>Links records decomposed from the same legacy Treatment or DeviceStatus.</summary>
    Guid? CorrelationId { get; set; }
}
