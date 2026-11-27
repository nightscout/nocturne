using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Identity;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A saved view of the People page (<see cref="PeopleView"/>). Not tenant-scoped: it spans every site its owner follows.
/// Its <c>subject_pin</c> policy admits only the rows of the subject a context is pinned to, so an unpinned or
/// tenant-pinned context reads none.
/// </summary>
[Table("people_views")]
public class PeopleViewEntity : IEntityTimestamped
{
    public const string Now = "now";
    public const string Review = "review";

    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Required]
    [MaxLength(PeopleViewLimits.NameLength)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The name in lower case, computed by the database, which the subject's unique index is over.</summary>
    [Column("name_key")]
    public string NameKey { get; set; } = string.Empty;

    [Column("position")]
    public int Position { get; set; }

    /// <summary>The <see cref="PeopleQuery"/>, read and written whole.</summary>
    [Column("query", TypeName = "jsonb")]
    public string Query { get; set; } = "{}";

    /// <summary><see cref="Now"/> or <see cref="Review"/>.</summary>
    [Required]
    [MaxLength(16)]
    [Column("lens")]
    public string Lens { get; set; } = Now;

    /// <summary>For the Review lens, its period in days; null for the Now lens.</summary>
    [Column("review_days")]
    public int? ReviewDays { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
