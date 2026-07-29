using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A scoped Do Not Disturb window (ADR 0004): a (scope, expiry) the user turned
/// on. <see cref="Id"/> is client-supplied so a window authored offline re-syncs
/// as an idempotent upsert. A <see cref="DndScope.All"/> window is tenant-wide DND
/// (it also drives the <c>do_not_disturb</c> condition); <see cref="DndScope.Lows"/>
/// / <see cref="DndScope.Highs"/> feed the suppression gate only.
/// </summary>
/// <remarks>
/// Rows are retained after they expire or are cleared (audit). Effective state is
/// resolved on read — not cleared, started, not past <see cref="EndsAt"/> — never a
/// stored "active" flag. The one-active-window-per-scope-per-instant invariant is
/// enforced on create (a same-scope window whose span *overlaps* is superseded, its
/// <see cref="ClearedBy"/> stamped <c>system:superseded</c>; one scheduled for a
/// non-overlapping span is left alone). <see cref="CreatedAt"/> is the server **receipt** time;
/// replay's "active at T" check uses it so it never retroactively suppresses the
/// offline-authoring gap.
/// </remarks>
[Table("dnd_windows")]
public class DndWindowEntity : ITenantScoped
{
    /// <summary>Client-supplied UUID. PK; a re-POST with the same id is idempotent.</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Identifier of the tenant this window belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Which alerts this window silences.</summary>
    [Column("scope")]
    public DndScope Scope { get; set; }

    /// <summary>When the user turned it on — the intent time (may predate <see cref="CreatedAt"/> for offline mutes).</summary>
    [Column("started_at")]
    public DateTime StartedAt { get; set; }

    /// <summary>Auto-expiry instant; null = until explicitly cleared.</summary>
    [Column("ends_at")]
    public DateTime? EndsAt { get; set; }

    /// <summary>Set when turned off early, or when superseded by a same-scope re-mute.</summary>
    [Column("cleared_at")]
    public DateTime? ClearedAt { get; set; }

    /// <summary>Who/what cleared it; the sentinel <c>system:superseded</c> marks a same-scope re-mute.</summary>
    [Column("cleared_by")]
    [MaxLength(128)]
    public string? ClearedBy { get; set; }

    /// <summary>What authored the window (e.g. <c>prelude-device</c>, <c>web</c>), for audit.</summary>
    [Column("source")]
    [MaxLength(128)]
    public string? Source { get; set; }

    /// <summary>Server receipt time (row insert). Replay resolves "active at T" against this.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
