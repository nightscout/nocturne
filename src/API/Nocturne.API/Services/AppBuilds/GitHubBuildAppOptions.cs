namespace Nocturne.API.Services.AppBuilds;

/// <summary>
/// The instance's GitHub App for app builds, bound to <c>GitHub:BuildApp</c>. Operator documentation:
/// the portal page <c>/docs/configuration/github-build-app</c>.
/// </summary>
public sealed class GitHubBuildAppOptions
{
    public const string SectionName = "GitHub:BuildApp";

    /// <summary>The App's URL name, as in <c>https://github.com/apps/{slug}</c>.</summary>
    public string? Slug { get; set; }

    /// <summary>Signs the App JWT as its <c>iss</c>, and identifies the App in the OAuth code exchange.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The App's private key, PEM.</summary>
    public string? PrivateKey { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Slug)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(PrivateKey);
}
