namespace Nocturne.API.Services;

/// <summary>
/// Shared configuration for the GitHub pull-request contribution flows
/// (translations and CMS content). Bound to the "GitHub" config section.
/// </summary>
public class GitHubContributionOptions
{
    /// <summary>
    /// PAT with contents+pull-request write access. Needs more privilege than
    /// IssuesPat, so it is a separate key; instances without one relay to
    /// nocturne.run like the support-issue flow.
    /// </summary>
    public string? ContributionsPat { get; set; }
    public string TranslationsRelayUrl { get; set; } = "https://nocturne.run/api/v4/translations/relay";
    public string ContentRelayUrl { get; set; } = "https://nocturne.run/api/v4/content/relay";
    /// <summary>
    /// Accept anonymous relayed contributions from other instances (the
    /// nocturne.run side of the relay). Requires ContributionsPat. Off by
    /// default so a regular instance never exposes an anonymous endpoint.
    /// </summary>
    public bool AcceptRelayedContributions { get; set; }
    public string Owner { get; set; } = GitHubApi.DefaultOwner;
    public string Repo { get; set; } = GitHubApi.DefaultRepo;
    public string BaseBranch { get; set; } = "main";
    public string CatalogDir { get; set; } = "src/Web/locales";
}
