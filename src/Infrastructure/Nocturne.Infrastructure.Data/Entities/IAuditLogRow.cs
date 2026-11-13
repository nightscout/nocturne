namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>The columns the mutation audit log and the read access log share, so one query reads either.</summary>
internal interface IAuditLogRow
{
    DateTime CreatedAt { get; }
    Guid? ActorSubjectId { get; }
    string? EntityType { get; }
}
