using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for a tenant's timezone timeline. Maps to the timezone_timeline table and to
/// <see cref="Nocturne.Core.Models.Timezones.TimezoneTimelineEntry"/>. Each row says "from
/// <see cref="EffectiveFrom"/> onward, the person was in <see cref="Timezone"/>", used to convert
/// fake-UTC connector data (e.g. Glooko) to true UTC.
/// </summary>
[Table("timezone_timeline")]
public class TimezoneTimelineEntity : ITenantScoped
{
    /// <summary>The unique identifier of the tenant this entry belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Primary key — UUID Version 7.</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Local wall-clock instant from which this zone takes effect. Stored without a time zone because
    /// it is a wall-clock value (DateTimeKind.Unspecified), not a UTC instant; the origin entry uses
    /// the minimum value to cover all earlier history.
    /// </summary>
    [Column("effective_from", TypeName = "timestamp without time zone")]
    public DateTime EffectiveFrom { get; set; }

    /// <summary>IANA timezone id (e.g. "Australia/Sydney").</summary>
    [Column("timezone")]
    [MaxLength(64)]
    [Required]
    public string Timezone { get; set; } = string.Empty;

    /// <summary>When this entry was created (UTC).</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this entry was last updated (UTC).</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
