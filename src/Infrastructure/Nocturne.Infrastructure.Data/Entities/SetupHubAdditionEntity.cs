using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.SetupHub;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A record a setup hub item created on the owner's behalf. The item may take back only what it
/// added; a record that was there before it stays out of its reach.
/// </summary>
[Table("setup_hub_additions")]
public class SetupHubAdditionEntity : ITenantScoped, ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("item_key")]
    [MaxLength(32)]
    public SetupHubItemKey ItemKey { get; set; }

    [Column("record_kind")]
    [MaxLength(32)]
    public SetupHubRecordKind RecordKind { get; set; }

    /// <summary>The id of the tracker definition or patient insulin the item created.</summary>
    [Column("record_id")]
    public Guid RecordId { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;
}
