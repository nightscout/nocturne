using Nocturne.Core.Models.Translations;

namespace Nocturne.Core.Models.Content;

public record ContentContributionRequest
{
    /// <summary>Repo-relative path of the .svx file to create or update.</summary>
    public required string Path { get; init; }
    public required string Content { get; init; }
    public required string Title { get; init; }
    public required ContributionContributorDto Contributor { get; init; }
    public string? Note { get; init; }
}

public record ContentContributionResponse
{
    public int PrNumber { get; init; }
    public string PrUrl { get; init; } = "";
    /// <summary>True when the file did not exist on the base branch.</summary>
    public bool Created { get; init; }
}
