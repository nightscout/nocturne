using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Ganss.Xss;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// Published releases.
/// </summary>
public interface IReleaseSource
{
    /// <summary>
    /// The newest release that is neither a draft nor a pre-release, or null when there is none.
    /// </summary>
    /// <exception cref="HttpRequestException">GitHub answered with an error other than 404.</exception>
    Task<ReleaseNotesDto?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The release of <paramref name="version"/>, or null when there is none.
    /// </summary>
    Task<ReleaseNotesDto?> GetByVersionAsync(ReleaseVersion version, CancellationToken cancellationToken);
}

/// <summary>
/// Reads published releases from GitHub without credentials. GitHub renders the notes to HTML
/// (the <c>html+json</c> media type), and they are sanitized here before anything stores or serves
/// them, so the frontend needs no markdown renderer.
/// </summary>
public class GitHubReleaseClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ReleaseCheckOptions> options,
    ILogger<GitHubReleaseClient> logger) : IReleaseSource
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    /// <inheritdoc />
    public Task<ReleaseNotesDto?> GetLatestAsync(CancellationToken cancellationToken) =>
        GetAsync("releases/latest", cancellationToken);

    /// <inheritdoc />
    public Task<ReleaseNotesDto?> GetByVersionAsync(ReleaseVersion version, CancellationToken cancellationToken) =>
        GetAsync($"releases/tags/v{Uri.EscapeDataString(version.ToString())}", cancellationToken);

    private async Task<ReleaseNotesDto?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var repo = $"repos/{Uri.EscapeDataString(options.Value.Owner)}/{Uri.EscapeDataString(options.Value.Repo)}";
        using var client = GitHubApi.CreateClient(httpClientFactory, pat: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{repo}/{path}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.html+json"));

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        // Any other error (a rate-limit 403 or 429, a 5xx) is a failed read, not an absent release.
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("GitHub returned {Status} for {Path}", (int)response.StatusCode, path);
            response.EnsureSuccessStatusCode();
        }

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
        if (release is null || release.Draft || release.Prerelease
            || !ReleaseVersion.TryParse(release.TagName, out var version))
            return null;

        return new ReleaseNotesDto
        {
            Version = version.ToString(),
            Name = release.Name,
            Url = release.HtmlUrl,
            PublishedAt = release.PublishedAt,
            NotesHtml = Sanitizer.Sanitize(release.BodyHtml ?? string.Empty, release.HtmlUrl),
        };
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedAttributes.Remove("style");
        // Release notes have no use for controls, and a form could imitate a sign-in prompt.
        foreach (var tag in new[] { "form", "input", "button", "select", "option", "optgroup", "textarea", "label", "fieldset", "legend" })
            sanitizer.AllowedTags.Remove(tag);
        sanitizer.AllowedAttributes.Remove("name");
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };
        return sanitizer;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body_html")]
        public string? BodyHtml { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}
