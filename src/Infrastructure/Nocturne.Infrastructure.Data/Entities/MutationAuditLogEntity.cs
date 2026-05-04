using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Audit log entry recording a mutation to a clinical entity.
/// Append-only — no updates or deletes.
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

    /// <summary>Primary key of the mutated entity.</summary>
    [Column("entity_id")]
    public Guid EntityId { get; set; }

    /// <summary>Mutation kind: create, update, delete, or restore.</summary>
    [Required]
    [Column("action")]
    [MaxLength(10)]
    public string Action { get; set; } = null!;

    /// <summary>JSONB diff for updates, full snapshot for deletes; null for creates.</summary>
    [Column("changes", TypeName = "jsonb")]
    public string? ChangesJson { get; set; }

    /// <summary>Authenticated user who performed the mutation, if known.</summary>
    [Column("subject_id")]
    public Guid? SubjectId { get; set; }

    /// <summary>Display name of the authenticated user, denormalized for historical retention.</summary>
    [Column("subject_name")]
    [MaxLength(128)]
    public string? SubjectName { get; set; }

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

    /// <summary>Request correlation ID for cross-service tracing.</summary>
    [Column("correlation_id")]
    [MaxLength(50)]
    public string? CorrelationId { get; set; }

    /// <summary>API route that handled the mutation (e.g. "PUT /api/v1/entries").</summary>
    [Column("endpoint")]
    [MaxLength(200)]
    public string? Endpoint { get; set; }

    /// <summary>UTC timestamp when this audit record was written.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
