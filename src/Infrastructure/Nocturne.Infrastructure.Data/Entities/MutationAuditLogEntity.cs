using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Contracts.Audit;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Audit log entry recording a mutation to a clinical entity.
/// Written once. Retention deletes it, and the actor's deletion clears its client address
/// (<see cref="Nocturne.Core.Contracts.Auth.ISubjectRepository.DeleteAsync"/>).
/// </summary>
[Table("mutation_audit_log")]
public class MutationAuditLogEntity : ITenantScoped
{
    /// <summary>Primary key (UUID v7).</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Owning tenant for RLS isolation.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>CLR type name of the mutated entity (e.g. "Entry", "Treatment").</summary>
    [Required]
    [Column("entity_type")]
    [MaxLength(100)]
    public string EntityType { get; set; } = null!;

    /// <summary>
    /// Primary key of the mutated entity; null for a <c>bulk_delete</c>, which describes a set of rows
    /// rather than one record.
    /// </summary>
    [Column("entity_id")]
    public Guid? EntityId { get; set; }

    /// <summary>Mutation kind: create, update, delete, restore, or bulk_delete.</summary>
    [Required]
    [Column("action")]
    [MaxLength(20)]
    public string Action { get; set; } = null!;

    /// <summary>
    /// JSONB diff for updates, full snapshot for deletes, <c>{count, scope}</c> for a bulk_delete;
    /// null for creates.
    /// </summary>
    [Column("changes", TypeName = "jsonb")]
    public string? ChangesJson { get; set; }

    /// <summary>The caller who performed the mutation, if known. See <see cref="Nocturne.Core.Contracts.Auth.IAuthAuditService"/>.</summary>
    [Column("actor_subject_id")]
    public Guid? ActorSubjectId { get; set; }

    /// <summary>Display name of the caller, denormalized for historical retention.</summary>
    [Column("actor_name")]
    [MaxLength(128)]
    public string? ActorName { get; set; }

    /// <summary>Authentication mechanism used (e.g. "Bearer", "ApiSecret", "Background").</summary>
    [Column("auth_type")]
    [MaxLength(50)]
    public string? AuthType { get; set; }

    /// <summary>Client IP address of the request that triggered the mutation.</summary>
    [Column("ip_address")]
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>OAuth grant or API token ID authorizing the request.</summary>
    [Column("token_id")]
    public Guid? TokenId { get; set; }

    /// <summary>Request trace identifier for cross-service tracing.</summary>
    [Column("correlation_id")]
    [MaxLength(50)]
    public string? TraceId { get; set; }

    /// <summary>API route that handled the mutation (e.g. "PUT /api/v1/entries").</summary>
    [Column("endpoint")]
    [MaxLength(200)]
    public string? Endpoint { get; set; }

    /// <summary>UTC timestamp when this audit record was written.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>Builds an audit row, taking every actor field from <paramref name="auditContext"/>.</summary>
    public static MutationAuditLogEntity Create(
        Guid tenantId,
        IAuditContext? auditContext,
        string entityType,
        Guid? entityId,
        string action,
        string? changesJson,
        DateTime createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        EntityType = entityType,
        EntityId = entityId,
        Action = action,
        ChangesJson = changesJson,
        ActorSubjectId = auditContext?.SubjectId,
        ActorName = auditContext?.SubjectName,
        AuthType = auditContext?.AuthType,
        IpAddress = auditContext?.IpAddress,
        TokenId = auditContext?.TokenId,
        TraceId = auditContext?.TraceId,
        Endpoint = auditContext?.Endpoint,
        CreatedAt = createdAt
    };
}
