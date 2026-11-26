using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Contracts.Telemetry;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>A row of <see cref="IPageViewCountStore"/>, which states what the table holds.</summary>
[Table("page_view_counts")]
public class PageViewCountEntity
{
    [Column("date")]
    public DateOnly Date { get; set; }

    [Column("route_id")]
    [MaxLength(200)]
    public string RouteId { get; set; } = string.Empty;

    [Column("count")]
    public long Count { get; set; }
}
