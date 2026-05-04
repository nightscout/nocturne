using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Audit log entry recording a read access to clinical data.
/// Append-only — no updates or deletes.
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

    /// <summary>Authenticated user who performed the read, if known.</summary>
    [Column("subject_id")]
    public Guid? SubjectId { get; set; }

    /// <summary>Display name of the authenticated user, denormalized for historical retention.</summary>
    [Column("subject_name")]
    [MaxLength(128)]
    public string? SubjectName { get; set; }

    /// <summary>Authentication mechanism used (e.g. "Bearer", "ApiSecret").</summary>
    [Column("auth_type")]
    [MaxLength(50)]
    public string? AuthType { get; set; }

    /// <summary>OAuth grant or API token ID authorizing the request.</summary>
    [Column("token_id")]
    public Guid? TokenId { get; set; }

    /// <summary>First 8 characters of the API secret SHA1 hash.</summary>
    [Column("api_secret_hash_prefix")]
    [MaxLength(8)]
    public string? ApiSecretHashPrefix { get; set; }

    /// <summary>Client IP address of the request.</summary>
    [Column("ip_address")]
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>User-Agent header from the request.</summary>
    [Column("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>API route that handled the read (e.g. "GET /api/v4/sensor-glucoses").</summary>
    [Required]
    [Column("endpoint")]
    [MaxLength(200)]
    public string Endpoint { get; set; } = null!;

    /// <summary>CLR type name of the accessed entity (e.g. "SensorGlucose").</summary>
    [Column("entity_type")]
    [MaxLength(100)]
    public string? EntityType { get; set; }

    /// <summary>Number of records returned in the response.</summary>
    [Column("record_count")]
    public int? RecordCount { get; set; }

    /// <summary>Sanitized query parameters from the request (JSONB).</summary>
    [Column("query_parameters", TypeName = "jsonb")]
    public string? QueryParametersJson { get; set; }

    /// <summary>Request correlation ID for cross-service tracing.</summary>
    [Column("correlation_id")]
    [MaxLength(50)]
    public string? CorrelationId { get; set; }

    /// <summary>HTTP status code of the response.</summary>
    [Column("status_code")]
    public int StatusCode { get; set; }

    /// <summary>UTC timestamp when this audit record was written.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
