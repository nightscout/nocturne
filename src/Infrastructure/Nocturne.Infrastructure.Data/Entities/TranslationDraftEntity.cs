using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for a user's in-progress translation of one message.
/// Maps to the translation_drafts table.
/// </summary>
[Table("translation_drafts")]
public class TranslationDraftEntity : ITenantScoped
{
    /// <summary>
    /// The unique identifier of the tenant this draft belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Unique identifier for the draft record
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// The subject (user) this draft belongs to
    /// </summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// BCP 47 tag of the locale being translated
    /// </summary>
    [Column("locale")]
    [MaxLength(16)]
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// msgctxt of the target catalog entry; empty string when uncontexted
    /// (part of the logical key, so never null)
    /// </summary>
    [Column("msgctxt")]
    [MaxLength(256)]
    public string Context { get; set; } = string.Empty;

    /// <summary>
    /// msgid of the target catalog entry
    /// </summary>
    [Column("msgid")]
    public string MsgId { get; set; } = string.Empty;

    /// <summary>
    /// Draft msgstr values: one for singular messages, nplurals for plurals
    /// </summary>
    [Column("translations", TypeName = "jsonb")]
    public List<string> Translations { get; set; } = [];

    /// <summary>
    /// When the draft was first created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the draft was last edited
    /// </summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
