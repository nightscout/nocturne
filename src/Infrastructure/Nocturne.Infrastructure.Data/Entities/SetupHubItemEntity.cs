using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.SetupHub;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One setup hub item listed for a tenant. A row exists from the first time the item is listed,
/// which is what keeps a listed item listed.
/// </summary>
[Table("setup_hub_items")]
public class SetupHubItemEntity : ITenantScoped, ISystemTimestamped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("item_key")]
    [MaxLength(32)]
    public SetupHubItemKey ItemKey { get; set; }

    [Column("state")]
    [MaxLength(16)]
    public SetupHubItemState State { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;
}
