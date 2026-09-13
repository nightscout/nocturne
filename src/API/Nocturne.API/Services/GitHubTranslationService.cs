using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Nocturne.API.Services;

public class GitHubTranslationOptions
{
    /// <summary>
    /// PAT with contents+pull-request write access. Needs more privilege than
    /// IssuesPat, so it is a separate key; instances without one relay to
    /// nocturne.run like the support-issue flow.
    /// </summary>
    public string? TranslationsPat { get; set; }
    public string TranslationsRelayUrl { get; set; } = "https://nocturne.run/api/v4/translations/relay";
    /// <summary>
    /// Accept anonymous relayed contributions from other instances (the
    /// nocturne.run side of the relay). Requires TranslationsPat. Off by
    /// default so a regular instance never exposes an anonymous endpoint.
    /// </summary>
    public bool AcceptRelayedContributions { get; set; }
    public string Owner { get; set; } = GitHubApi.DefaultOwner;
    public string Repo { get; set; } = GitHubApi.DefaultRepo;
    public string BaseBranch { get; set; } = "main";
    public string CatalogDir { get; set; } = "src/Web/locales";
}

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

/// <summary>
/// Mirrors GitHubIssueService — keep the two in step.
/// </summary>
public partial class GitHubTranslationService(
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubTranslationOptions> options,
    ILogger<GitHubTranslationService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public bool HasLocalPat => !string.IsNullOrEmpty(options.Value.TranslationsPat);

    public bool AcceptsRelay => options.Value.AcceptRelayedContributions && HasLocalPat;

    public async Task<TranslationContributionResponse> SubmitAsync(
        TranslationContributionRequest request, CancellationToken ct)
    {
        var opts = options.Value;
        using var client = GitHubApi.CreateClient(httpClientFactory, options.Value.TranslationsPat);

        var catalogPath = $"{opts.CatalogDir}/{request.Locale}.po";
        var (catalogText, fileSha) = await GetCatalogAsync(client, catalogPath, ct);

        var edits = request.Entries.ToDictionary(
            e => (e.Context ?? "", e.MsgId),
            e => (IReadOnlyList<string>)e.Translations);
        var result = PoCatalogEditor.ApplyTranslations(catalogText, edits);

        if (result.Applied == 0)
            throw new TranslationContributionRejectedException(
                "No contributed message matched the current catalog. The catalog may have changed; refresh and try again.");

        var branch = $"translations/{request.Locale}-{Guid.NewGuid().ToString("N")[..12]}";
        var baseSha = await GetBranchShaAsync(client, opts.BaseBranch, ct);
        await CreateBranchAsync(client, branch, baseSha, ct);

        int prNumber;
        string prUrl;
        try
        {
            await CommitCatalogAsync(client, catalogPath, branch, fileSha, result.Text, request, result.Applied, ct);
            (prNumber, prUrl) = await OpenPullRequestAsync(client, branch, request, result, ct);
        }
        catch
        {
            await TryDeleteBranchAsync(client, branch);
            throw;
        }

        var safeLocaleForLog = SanitizeForLog(request.Locale);
        logger.LogInformation(
            "Opened translation PR #{PrNumber} for {Locale}: {Applied} applied, {Unmatched} unmatched",
            prNumber, safeLocaleForLog, result.Applied, result.Unmatched.Count);

        return new TranslationContributionResponse
        {
            PrNumber = prNumber,
            PrUrl = prUrl,
            Applied = result.Applied,
            Unmatched = [.. result.Unmatched],
        };
    }

    public async Task<TranslationContributionResponse> RelayAsync(
        TranslationContributionRequest request, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync(options.Value.TranslationsRelayUrl, request, JsonOpts, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Translation relay error: {StatusCode} {Error}", response.StatusCode, error);
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
                throw new TranslationContributionRejectedException(
                    "No contributed message matched the current catalog. The catalog may have changed; refresh and try again.");
            throw new InvalidOperationException($"Translation relay error: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<TranslationContributionResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Failed to deserialize relay response");
    }

    private async Task<(string Text, string Sha)> GetCatalogAsync(
        HttpClient client, string path, CancellationToken ct)
    {
        var opts = options.Value;
        // The contents API caps files at 1 MB; the largest catalog is ~0.9 MB
        // today. If catalogs outgrow that, switch to the blobs API.
        var response = await client.GetAsync(
            $"/repos/{opts.Owner}/{opts.Repo}/contents/{path}?ref={Uri.EscapeDataString(opts.BaseBranch)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new TranslationContributionRejectedException($"No catalog exists for this locale ({path}).");
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("GitHub API error fetching catalog: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"GitHub API error: {response.StatusCode}");
        }

        var file = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize GitHub content response");
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(file.Content.Replace("\n", "")));
        return (text, file.Sha);
    }

    private async Task<string> GetBranchShaAsync(HttpClient client, string branch, CancellationToken ct)
    {
        var opts = options.Value;
        var response = await client.GetAsync(
            $"/repos/{opts.Owner}/{opts.Repo}/git/ref/heads/{branch}", ct);
        response.EnsureSuccessStatusCode();
        var reference = await response.Content.ReadFromJsonAsync<GitHubRefResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize GitHub ref response");
        return reference.Object.Sha;
    }

    private async Task CreateBranchAsync(HttpClient client, string branch, string sha, CancellationToken ct)
    {
        var opts = options.Value;
        var response = await client.PostAsJsonAsync(
            $"/repos/{opts.Owner}/{opts.Repo}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("GitHub API error creating branch: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"GitHub API error: {response.StatusCode}");
        }
    }

    private async Task CommitCatalogAsync(
        HttpClient client, string path, string branch, string fileSha,
        string newText, TranslationContributionRequest request, int applied, CancellationToken ct)
    {
        var opts = options.Value;
        var message = BuildCommitMessage(request, applied);

        var response = await client.PutAsJsonAsync(
            $"/repos/{opts.Owner}/{opts.Repo}/contents/{path}",
            new
            {
                message,
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(newText)),
                sha = fileSha,
                branch,
            }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("GitHub API error committing catalog: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"GitHub API error: {response.StatusCode}");
        }
    }

    private async Task<(int Number, string Url)> OpenPullRequestAsync(
        HttpClient client, string branch, TranslationContributionRequest request,
        PoEditResult result, CancellationToken ct)
    {
        var opts = options.Value;
        var response = await client.PostAsJsonAsync(
            $"/repos/{opts.Owner}/{opts.Repo}/pulls",
            new
            {
                title = $"i18n({request.Locale}): {result.Applied} translation{(result.Applied == 1 ? "" : "s")} via in-app contribution",
                head = branch,
                @base = opts.BaseBranch,
                body = BuildPrBody(request, result),
                maintainer_can_modify = true,
            }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("GitHub API error opening PR: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"GitHub API error: {response.StatusCode}");
        }

        var pr = await response.Content.ReadFromJsonAsync<GitHubPullResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize GitHub PR response");
        return (pr.Number, pr.HtmlUrl);
    }

    /// <summary>Defense in depth behind controller validation.</summary>
    internal static string SanitizeMetadata(string value) =>
        new([.. value.Trim().Where(c => !char.IsControl(c) && c is not '<' and not '>')]);

    /// <summary>
    /// Renders a contributor-supplied display name for a sink GitHub gives
    /// side effects to. The name arrives from an anonymous relay, so
    /// <c>Jane fixes #12 cc @someone</c> would otherwise auto-close an issue
    /// and notify arbitrary users from the upstream PR body and from the
    /// commit message. A commit message is not markdown — a backslash escape
    /// would render literally there — so the reference-carrying characters
    /// are dropped instead of escaped when <paramref name="markdown"/> is
    /// false. The backslash is escaped first so a submitted <c>\</c> cannot
    /// consume the escape that follows it.
    ///
    /// <c>#</c> handling covers <c>#12</c> and <c>owner/repo#12</c>, but
    /// GitHub resolves two further reference forms that carry no <c>#</c> and
    /// no <c>@</c>: the <c>GH-12</c> shorthand and a full issue or pull URL.
    /// Both fit inside a name and both honour closing keywords, so both are
    /// removed outright — a person's name legitimately contains neither.
    /// </summary>
    internal static string RenderName(string name, bool markdown)
    {
        var value = SanitizeMetadata(name);
        value = markdown
            ? value.Replace("\\", "\\\\").Replace("@", "\\@").Replace("#", "\\#").Replace("`", "\\`")
            : new string([.. value.Where(c => c is not '@' and not '#')]);

        // Last, because dropping a "#" above can splice a reference back
        // together ("htt#ps://…", "GH#-1"). Neither pass can recreate the
        // other's target: URL removal only deletes, and separating "GH" from
        // its digits cannot produce a "://".
        value = UrlReference().Replace(value, "");
        return GitHubShorthandReference().Replace(value, "$1 ").Trim();
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlReference();

    /// <summary>
    /// The <c>GH-123</c> shorthand. Only the hyphen is replaced: the autolink
    /// requires <c>GH-</c> immediately followed by a digit, so a space between
    /// them leaves nothing for GitHub to resolve while the name stays readable.
    /// </summary>
    [GeneratedRegex(@"(GH)-(?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubShorthandReference();

    /// <summary>
    /// Renders free-text contributor input inside a fenced code block.
    /// Nothing inside a code fence is interpreted, which neutralizes the
    /// references <see cref="RenderName"/> describes plus block markup and
    /// raw HTML at once — provided the note cannot close the fence, so the
    /// fence runs one backtick longer than the longest backtick run in it.
    /// </summary>
    internal static string RenderNoteAsCodeFence(string note)
    {
        var text = StripControlChars(note).ReplaceLineEndings("\n");
        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));

        var sb = new StringBuilder();
        sb.AppendLine(fence);
        foreach (var line in text.Split('\n'))
            sb.AppendLine(line);
        sb.AppendLine(fence);
        return sb.ToString();
    }

    private static int LongestBacktickRun(string value)
    {
        int longest = 0, run = 0;
        foreach (var c in value)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest)
                longest = run;
        }
        return longest;
    }

    /// <summary>Drops C0/C1 control characters but keeps line structure.</summary>
    private static string StripControlChars(string value) =>
        new([.. value.Where(c => !char.IsControl(c) || c is '\r' or '\n')]);

    internal static string BuildCommitMessage(TranslationContributionRequest request, int applied)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"chore(i18n): {request.Locale} translations via in-app contribution");
        sb.AppendLine();
        sb.AppendLine($"Applies {applied} message{(applied == 1 ? "" : "s")} contributed by {RenderName(request.Contributor.Name, markdown: false)}.");

        var coAuthor = CoAuthorTrailer(request.Contributor);
        if (coAuthor is not null)
        {
            sb.AppendLine();
            sb.AppendLine(coAuthor);
        }

        return sb.ToString();
    }

    internal static string? CoAuthorTrailer(TranslationContributorDto contributor)
    {
        if (!string.IsNullOrWhiteSpace(contributor.GitHubUsername))
        {
            var username = SanitizeMetadata(contributor.GitHubUsername);
            return $"Co-authored-by: {username} <{username}@users.noreply.github.com>";
        }

        if (!string.IsNullOrWhiteSpace(contributor.Email))
            return $"Co-authored-by: {RenderName(contributor.Name, markdown: false)} <{SanitizeMetadata(contributor.Email)}>";

        return null;
    }

    internal static string BuildPrBody(TranslationContributionRequest request, PoEditResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Translation contribution for `{request.Locale}` submitted through the in-app translation mode.");
        sb.AppendLine();
        sb.AppendLine($"- **Contributor:** {RenderName(request.Contributor.Name, markdown: true)}"
            + (string.IsNullOrWhiteSpace(request.Contributor.GitHubUsername)
                ? ""
                : $" (@{SanitizeMetadata(request.Contributor.GitHubUsername)})"));
        sb.AppendLine($"- **Messages updated:** {result.Applied}");

        if (result.Unmatched.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{result.Unmatched.Count} entr{(result.Unmatched.Count == 1 ? "y" : "ies")} no longer in the catalog (skipped)</summary>");
            sb.AppendLine();
            // Backticks are dropped rather than escaped: a backslash is literal
            // inside a CommonMark code span, so an escaped backtick would still
            // close it.
            foreach (var entry in result.Unmatched.Take(50))
            {
                var display = entry.MsgId.Replace("\r", "").Replace("\n", "\\n").Replace("`", "");
                if (display.Length > 120)
                    display = display[..120] + "…";
                var context = entry.Context.Length == 0
                    ? ""
                    : $" (context: {SanitizeMetadata(entry.Context)})";
                sb.AppendLine($"- `{display}`{context}");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            sb.AppendLine();
            sb.AppendLine("## Contributor note");
            sb.AppendLine();
            sb.Append(RenderNoteAsCodeFence(request.Note));
        }

        return sb.ToString();
    }

    private async Task TryDeleteBranchAsync(HttpClient client, string branch)
    {
        var opts = options.Value;
        try
        {
            await client.DeleteAsync(
                $"/repos/{opts.Owner}/{opts.Repo}/git/refs/heads/{branch}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up branch {Branch} after error", branch);
        }
    }

    private record GitHubContentResponse
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = "";
        [JsonPropertyName("sha")]
        public string Sha { get; init; } = "";
    }

    private record GitHubRefResponse
    {
        [JsonPropertyName("object")]
        public GitHubRefObject Object { get; init; } = new();
    }

    private record GitHubRefObject
    {
        [JsonPropertyName("sha")]
        public string Sha { get; init; } = "";
    }

    private record GitHubPullResponse
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";
    }

    private static string SanitizeForLog(string value) =>
        value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

public class TranslationContributionRejectedException(string message) : Exception(message);
