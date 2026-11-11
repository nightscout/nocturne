using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Audit log entry recording a read access to clinical data.
/// Written once. Retention deletes it, and the actor's deletion clears its client address and user agent
/// (<see cref="Nocturne.Core.Contracts.Auth.ISubjectRepository.DeleteAsync"/>).
/// </summary>
[Table("read_access_log")]
public class ReadAccessLogEntity : ITenantScoped
{
    /// <summary>Primary key (UUID v7).</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Owning tenant for RLS isolation.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The caller who performed the read, if known. See <see cref="Nocturne.Core.Contracts.Auth.IAuthAuditService"/>.</summary>
    [Column("actor_subject_id")]
    public Guid? ActorSubjectId { get; set; }

    /// <summary>Display name of the caller, denormalized for historical retention.</summary>
    [Column("actor_name")]
    [MaxLength(128)]
    [FitToColumn]
    public string? ActorName { get; set; }

    /// <summary>Authentication mechanism used (e.g. "Bearer", "ApiSecret").</summary>
    [Column("auth_type")]
    [MaxLength(50)]
    [FitToColumn]
    public string? AuthType { get; set; }

    /// <summary>OAuth grant or API token ID authorizing the request.</summary>
    [Column("token_id")]
    public Guid? TokenId { get; set; }

    /// <summary>
    /// Identifies the credential behind the read when it has one of its own — an API secret or the
    /// instance key. Never holds key material or any part of a stored digest.
    /// </summary>
    /// <seealso cref="Nocturne.Core.Models.Authorization.AuthContext.CredentialFingerprint"/>
    [Column("credential_fingerprint")]
    [MaxLength(16)]
    public string? CredentialFingerprint { get; set; }

    /// <summary>Client IP address of the request.</summary>
    [Column("ip_address")]
    [MaxLength(45)]
    [FitToColumn]
    public string? IpAddress { get; set; }

    /// <summary>User-Agent header from the request.</summary>
    [Column("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>API route that handled the read (e.g. "GET /api/v4/sensor-glucoses").</summary>
    [Required]
    [Column("endpoint")]
    [MaxLength(200)]
    [FitToColumn]
    public string Endpoint { get; set; } = null!;

    /// <summary>CLR type name of the accessed entity (e.g. "SensorGlucose").</summary>
    [Column("entity_type")]
    [MaxLength(100)]
    [FitToColumn]
    public string? EntityType { get; set; }

    /// <summary>Number of records returned in the response.</summary>
    [Column("record_count")]
    public int? RecordCount { get; set; }

    /// <summary>Sanitized query parameters from the request (JSONB).</summary>
    [Column("query_parameters", TypeName = "jsonb")]
    public string? QueryParametersJson { get; set; }

    /// <summary>Request trace identifier for cross-service tracing.</summary>
    [Column("correlation_id")]
    [MaxLength(50)]
    [FitToColumn]
    public string? TraceId { get; set; }

    /// <summary>HTTP status code of the response.</summary>
    [Column("status_code")]
    public int StatusCode { get; set; }

    /// <summary>UTC timestamp when this audit record was written.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
