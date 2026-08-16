using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Content;
using Nocturne.Core.Models.Content;

namespace Nocturne.API.Services;

/// <summary>
/// Turns a CMS content contribution into an upstream pull request: fetch the
/// current file (if any), commit the new content to a branch, open a PR with
/// contributor attribution. Instances without a PAT relay to nocturne.run.
/// Shares GitHubContributionOptions ("GitHub" config section): same PAT,
/// repo, base branch and relay opt-in as translation contributions.
/// </summary>
public partial class GitHubContentService(
    GitHubPrClient prClient,
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubContributionOptions> options,
    ILogger<GitHubContentService> logger) : IContentContributionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Contributions may only touch portal content: blog and docs .svx files
    /// with conservative path segments. Anything else is rejected before any
    /// GitHub call — the relay ingress is reachable anonymously.
    /// </summary>
    // Each segment alternates alphanumerics and single separators. Traversal
    // is already impossible — a segment must start [a-z0-9], so "/../" can
    // never match — and BranchSlug maps every non-alphanumeric to a hyphen, so
    // even "a..b" would yield the legal ref "content/a--b-...". What the rule
    // buys is a predictable one-to-one shape: the file stem, the generated
    // slug and the branch name stay recognisably the same string.
    [GeneratedRegex(@"\Asrc/Web/packages/portal/src/content/(blog|docs)(/[a-z0-9](?:[._-]?[a-z0-9])*)*/[a-z0-9](?:[._-]?[a-z0-9])*\.svx\z")]
    public static partial Regex AllowedPathPattern();

    public bool HasLocalPat => !string.IsNullOrEmpty(options.Value.ContributionsPat);

    public bool AcceptsRelay => options.Value.AcceptRelayedContributions && HasLocalPat;

    public async Task<ContentContributionResponse> SubmitAsync(
        ContentContributionRequest request, CancellationToken ct)
    {
        var opts = options.Value;
        if (!AllowedPathPattern().IsMatch(request.Path))
            throw new ContributionRejectedException("This path cannot be modified through content contributions.");

        using var client = prClient.CreateClient(opts.ContributionsPat);

        var existing = await prClient.GetFileAsync(client, opts.Owner, opts.Repo, request.Path, opts.BaseBranch, ct);
        if (existing is { } file && file.Text == request.Content)
            throw new ContributionRejectedException("The content is identical to the published version.");

        var branch = $"content/{BranchSlug(request.Path)}-{Guid.NewGuid().ToString("N")[..12]}";
        var baseSha = await prClient.GetBranchShaAsync(client, opts.Owner, opts.Repo, opts.BaseBranch, ct);
        await prClient.CreateBranchAsync(client, opts.Owner, opts.Repo, branch, baseSha, ct);

        int prNumber;
        string prUrl;
        try
        {
            await prClient.CommitFileAsync(
                client, opts.Owner, opts.Repo, request.Path, branch,
                existing?.Sha, request.Content, BuildCommitMessage(request, existing is null), ct);
            (prNumber, prUrl) = await prClient.OpenPullRequestAsync(
                client, opts.Owner, opts.Repo, branch, opts.BaseBranch,
                $"content: {GitHubPrClient.SanitizeMetadata(request.Title)}",
                BuildPrBody(request, existing is null), ct);
        }
        catch
        {
            await prClient.TryDeleteBranchAsync(client, opts.Owner, opts.Repo, branch);
            throw;
        }

        logger.LogInformation(
            "Opened content PR #{PrNumber} for {Path} ({Mode})",
            prNumber, request.Path, existing is null ? "create" : "update");

        return new ContentContributionResponse
        {
            PrNumber = prNumber,
            PrUrl = prUrl,
            Created = existing is null,
        };
    }

    public async Task<ContentContributionResponse> RelayAsync(
        ContentContributionRequest request, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync(options.Value.ContentRelayUrl, request, JsonOpts, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Content relay error: {StatusCode} {Error}", response.StatusCode, error);
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
                // Forward the relay's own reason so the contributor can tell
                // "identical to published" from "path not allowed".
                throw new ContributionRejectedException(
                    GitHubPrClient.RelayRejectionDetail(error) ?? "The contribution was rejected by the relay.");
            throw new InvalidOperationException($"Content relay error: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<ContentContributionResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Failed to deserialize relay response");
    }

    /// <summary>
    /// Git refs cannot contain "..", a leading/trailing dot or a trailing
    /// ".lock", so the file stem is reduced to alphanumerics and hyphens
    /// before it becomes a branch name.
    /// </summary>
    internal static string BranchSlug(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var slug = new string([.. stem.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return slug.Length == 0 ? "content" : slug;
    }

    internal static string BuildCommitMessage(ContentContributionRequest request, bool created)
    {
        var sb = new StringBuilder();
        var slug = Path.GetFileNameWithoutExtension(request.Path);
        sb.AppendLine($"content: {(created ? "add" : "update")} {slug}");
        sb.AppendLine();
        sb.AppendLine($"Contributed by {ContributionValidation.RenderName(request.Contributor.Name, markdown: false)} via the in-app content studio.");

        var coAuthor = GitHubPrClient.CoAuthorTrailer(request.Contributor);
        if (coAuthor is not null)
        {
            sb.AppendLine();
            sb.AppendLine(coAuthor);
        }

        return sb.ToString();
    }

    internal static string BuildPrBody(ContentContributionRequest request, bool created)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{(created ? "New" : "Updated")} content proposed through the in-app content studio.");
        sb.AppendLine();
        sb.AppendLine($"- **File:** `{request.Path}`");
        sb.AppendLine($"- **Contributor:** {ContributionValidation.RenderName(request.Contributor.Name, markdown: true)}"
            + (string.IsNullOrWhiteSpace(request.Contributor.GitHubUsername)
                ? ""
                : $" (@{GitHubPrClient.SanitizeMetadata(request.Contributor.GitHubUsername)})"));

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            sb.AppendLine();
            sb.AppendLine("## Contributor note");
            sb.AppendLine();
            sb.Append(ContributionValidation.RenderNoteAsCodeFence(request.Note));
        }

        return sb.ToString();
    }
}
