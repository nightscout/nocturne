namespace Nocturne.Core.Models.Translations;

public record TranslationEntryDto
{
    public required string MsgId { get; init; }
    public string? Context { get; init; }
    /// <summary>One value for singular messages, nplurals values for plural ones.</summary>
    public required List<string> Translations { get; init; }
}

public record TranslationContributorDto
{
    public required string Name { get; init; }
    public string? GitHubUsername { get; init; }
    public string? Email { get; init; }
}

public record TranslationContributionRequest
{
    public required string Locale { get; init; }
    public required List<TranslationEntryDto> Entries { get; init; }
    public required TranslationContributorDto Contributor { get; init; }
    public string? Note { get; init; }
}

public record TranslationUnmatchedEntry
{
    public required string MsgId { get; init; }
    /// <summary>msgctxt of the entry; empty string when uncontexted.</summary>
    public string Context { get; init; } = "";
}

public record TranslationContributionResponse
{
    public int PrNumber { get; init; }
    public string PrUrl { get; init; } = "";
    public int Applied { get; init; }
    public List<TranslationUnmatchedEntry> Unmatched { get; init; } = [];
}
