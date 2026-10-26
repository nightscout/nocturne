namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// The release check: where releases are published and how to reach the deployment's Watchtower.
/// </summary>
public class ReleaseCheckOptions
{
    public const string SectionName = "ReleaseCheck";

    /// <summary>
    /// Off for a hosted deployment, whose tenant owners do not install updates.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Owner { get; set; } = GitHubApi.DefaultOwner;

    public string Repo { get; set; } = GitHubApi.DefaultRepo;

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(6);

    public WatchtowerOptions Watchtower { get; set; } = new();
}

/// <summary>
/// The Watchtower container of the published compose bundle.
/// </summary>
public class WatchtowerOptions
{
    /// <summary>
    /// Its HTTP API on the stack's network. The compose service name resolves only while the
    /// container runs, which is what detects it.
    /// </summary>
    public string Url { get; set; } = "http://watchtower:8080";

    /// <summary>
    /// The bundle's <c>WATCHTOWER_HTTP_API_TOKEN</c>. Without it Watchtower is only detected.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// The bundle's <c>WATCHTOWER_POLL_INTERVAL</c>. Watchtower's API does not report it.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 86400;
}
