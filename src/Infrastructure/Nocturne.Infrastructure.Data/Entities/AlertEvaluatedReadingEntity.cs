using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One row per tenant: the newest reading its enabled rules were last evaluated against by a pass
/// that completed (<see cref="Core.Contracts.Alerts.IEvaluatedReadingStore"/>).
/// </summary>
[Table("alert_evaluated_readings")]
public class AlertEvaluatedReadingEntity : ITenantScoped
{
    [Key]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("reading_timestamp")]
    public DateTime ReadingTimestamp { get; set; }

    [Column("reading_mgdl")]
    public double ReadingMgdl { get; set; }

    [Column("reading_trend_rate")]
    public double? ReadingTrendRate { get; set; }

    [Column("evaluated_at")]
    public DateTime EvaluatedAt { get; set; }
}
