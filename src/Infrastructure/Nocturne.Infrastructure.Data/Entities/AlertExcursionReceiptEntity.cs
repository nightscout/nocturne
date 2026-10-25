using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A record that someone saw an alarm: one per viewer per excursion. It changes nothing about the
/// alert or anyone's escalation, and it outlives the excursion closing, so the household can see
/// afterwards who saw it and when.
/// </summary>
/// <remarks>
/// Not <see cref="IAuditable"/>: the row is itself the record, and a public link or a clock writes
/// it without an account, which would otherwise append an audit row per press.
/// </remarks>
[Table("alert_excursion_receipts")]
public class AlertExcursionReceiptEntity : ITenantScoped
{
    [Key]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("alert_excursion_id")]
    public Guid AlertExcursionId { get; set; }

    [Column("actor_kind")]
    [MaxLength(32)]
    public AlertReceiptActorKind ActorKind { get; set; }

    /// <summary>
    /// The member's subject, the guest link's grant, the Public subject or the clock face, by
    /// <see cref="ActorKind"/>.
    /// </summary>
    [Column("actor_id")]
    public Guid ActorId { get; set; }

    /// <summary>
    /// The guest link's label or the clock face's name as it was when the alarm was seen; null for a
    /// member, named from their subject when read, and for the public link.
    /// </summary>
    [Column("actor_label")]
    [MaxLength(255)]
    public string? ActorLabel { get; set; }

    [Column("seen_at")]
    public DateTime SeenAt { get; set; }

    public AlertExcursionEntity? AlertExcursion { get; set; }
}
