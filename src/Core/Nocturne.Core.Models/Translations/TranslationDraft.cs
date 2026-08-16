namespace Nocturne.Core.Models.Translations;

/// <summary>
/// A user's in-progress translation for one message, stored server-side so
/// drafts survive sessions and devices until they are submitted upstream.
/// </summary>
public record TranslationDraft
{
    public Guid Id { get; init; }
    public required string Locale { get; init; }
    /// <summary>msgctxt of the target entry; empty string when uncontexted.</summary>
    public string Context { get; init; } = "";
    public required string MsgId { get; init; }
    /// <summary>One value for singular messages, nplurals values for plural ones.</summary>
    public required List<string> Translations { get; init; }
    public DateTime UpdatedAt { get; init; }
}
