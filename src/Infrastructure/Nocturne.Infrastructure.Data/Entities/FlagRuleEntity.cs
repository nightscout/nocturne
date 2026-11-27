using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Constants;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A flag rule (<see cref="Nocturne.Core.Models.Identity.FlagRule"/>). Not tenant-scoped: it spans every site its
/// owner follows. Its owner is a subject or a team, exactly one of the two, so a team's rules sit beside a person's
/// without moving them. Its <c>subject_pin</c> policy admits only the rows of the subject a context is pinned to, so
/// an unpinned or tenant-pinned context reads none.
/// </summary>
[Table("flag_rules")]
public class FlagRuleEntity : IEntityTimestamped
{
    public const string Below = "below";
    public const string Above = "above";

    public const string Info = "info";
    public const string Warn = "warn";
    public const string Hazard = "hazard";

    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("subject_id")]
    public Guid? SubjectId { get; set; }

    [Column("team_id")]
    public Guid? TeamId { get; set; }

    [Required]
    [MaxLength(ColumnLengths.FlagRule.Dataset)]
    [Column("dataset")]
    public string Dataset { get; set; } = string.Empty;

    [Required]
    [MaxLength(ColumnLengths.FlagRule.Field)]
    [Column("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>The last N days of a period fact; null for a live fact.</summary>
    [Column("days")]
    public int? Days { get; set; }

    /// <summary><see cref="Below"/> or <see cref="Above"/>.</summary>
    [Required]
    [MaxLength(16)]
    [Column("comparison")]
    public string Comparison { get; set; } = Above;

    [Column("value")]
    public double Value { get; set; }

    /// <summary><see cref="Info"/>, <see cref="Warn"/> or <see cref="Hazard"/>.</summary>
    [Required]
    [MaxLength(16)]
    [Column("level")]
    public string Level { get; set; } = Warn;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
