using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One member's mute of one open excursion: that member's in-app notifications and registered
/// client devices get no further deliveries for it, while everyone else's escalation continues.
/// The close handler deletes an excursion's mutes. A test fire's excursion closes by time without
/// it, so its mutes stay until the excursion row goes; every reader filters to open excursions, so
/// they never apply.
/// </summary>
/// <seealso cref="Core.Contracts.Alerts.IAlertAcknowledgementService.AcknowledgeExcursionAsync"/>
[Table("alert_excursion_mutes")]
public class AlertExcursionMuteEntity : ITenantScoped, IEntityCreated
{
    [Key]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The member who muted the excursion.</summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("alert_excursion_id")]
    public Guid AlertExcursionId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AlertExcursionEntity? AlertExcursion { get; set; }

    public SubjectEntity? Subject { get; set; }
}
