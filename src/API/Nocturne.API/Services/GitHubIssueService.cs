using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Nocturne.API.Services;

public class GitHubIssueOptions
{
    public string? IssuesPat { get; set; }
    public string RelayUrl { get; set; } = "https://nocturne.run/api/v4/support/issues";
    public string Owner { get; set; } = GitHubApi.DefaultOwner;
    public string Repo { get; set; } = GitHubApi.DefaultRepo;

    /// <summary>
    /// Branch of <see cref="Repo"/> that screenshot attachments are committed
    /// to via the contents API, then embedded in the issue body by raw URL.
    /// GitHub has no API for uploading images directly into an issue, so this
    /// is the only way to make screenshots render inline. The branch must
    /// exist (an orphan branch keeps the history separate) and the PAT needs
    /// contents write access. When empty, screenshots are not uploaded and the
    /// issue body notes how many were attached.
    /// </summary>
    public string? AssetsBranch { get; set; } = "support-assets";
}

public record CreateIssueRequest
{
    public required string Template { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? StepsToReproduce { get; init; }
    public string? ExpectedBehavior { get; init; }
    public string? ActualBehavior { get; init; }
    public string? CgmSource { get; init; }
    public string? TimeRange { get; init; }
    public required string DiagnosticInfo { get; init; }
}

public record CreateIssueResponse
{
    public int IssueNumber { get; init; }
    public string IssueUrl { get; init; } = "";
}

public record FallbackUrlResponse
{
    public string Url { get; init; } = "";
}

public class SupportConfigResponse
{
    public SupportChannelConfig? AccountBilling { get; set; }
}

public class SupportChannelConfig
{
    public string Mode { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Label { get; set; }
}

public class GitHubIssueService(
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubIssueOptions> options,
    ILogger<GitHubIssueService> logger)
{
    private static readonly Dictionary<string, string> TemplateLabels = new()
    {
        ["bug"] = "bug",
        ["feature"] = "enhancement",
        ["data-issue"] = "data-issue",
        ["account"] = "account",
    };

    public bool HasLocalPat => !string.IsNullOrEmpty(options.Value.IssuesPat);

    public async Task<CreateIssueResponse> CreateIssueAsync(
        CreateIssueRequest request,
        IReadOnlyList<(string FileName, string ContentType, Stream Content)> images,
        CancellationToken ct)
    {
        var imageUrls = await UploadImagesAsync(images, ct);
        var body = BuildIssueBody(request, imageUrls, images.Count);
        var label = TemplateLabels.GetValueOrDefault(request.Template, "bug");

        using var client = GitHubApi.CreateClient(httpClientFactory, options.Value.IssuesPat);
        var ghRequest = new GitHubCreateIssueRequest
        {
            Title = request.Title,
            Body = body,
            Labels = [label],
        };

        var json = JsonSerializer.Serialize(ghRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var opts = options.Value;

        var response = await client.PostAsync(
            $"/repos/{opts.Owner}/{opts.Repo}/issues", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("GitHub API error creating issue: {StatusCode} {Error}",
                response.StatusCode, error);
            throw new InvalidOperationException($"GitHub API error: {response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<GitHubCreateIssueResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize GitHub response");

        return new CreateIssueResponse
        {
            IssueNumber = result.Number,
            IssueUrl = result.HtmlUrl,
        };
    }

    /// <summary>
    /// Forward a complete multipart request to the relay (nocturne.run) when no local PAT is configured.
    /// </summary>
    public async Task<CreateIssueResponse> RelayAsync(
        HttpContent originalContent, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(options.Value.RelayUrl, originalContent, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Relay error: {StatusCode} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"Relay error: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<CreateIssueResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize relay response");
    }

    /// <summary>
    /// Commits each image to the assets branch of the issues repo via the
    /// GitHub contents API and returns the resulting raw URLs for embedding.
    /// GitHub has no API for uploading images directly into an issue, so this
    /// is the only way to make screenshots render inline. Returns an empty
    /// list when no assets branch is configured; the issue body then records
    /// the attachment count.
    /// </summary>
    private async Task<List<string>> UploadImagesAsync(
        IReadOnlyList<(string FileName, string ContentType, Stream Content)> images,
        CancellationToken ct)
    {
        var urls = new List<string>();
        if (images.Count == 0) return urls;

        var opts = options.Value;
        if (string.IsNullOrEmpty(opts.AssetsBranch))
        {
            logger.LogWarning(
                "GitHub:AssetsBranch is not configured; {Count} screenshot(s) will not be uploaded",
                images.Count);
            return urls;
        }

        using var client = GitHubApi.CreateClient(httpClientFactory, options.Value.IssuesPat);

        foreach (var (fileName, _, imageContent) in images)
        {
            using var buffer = new MemoryStream();
            await imageContent.CopyToAsync(buffer, ct);

            var path = $"screenshots/{DateTime.UtcNow:yyyy-MM}/{Guid.NewGuid():N}-{SanitizeFileName(fileName)}";
            var payload = JsonSerializer.Serialize(new GitHubCreateContentRequest
            {
                Message = $"Add support issue screenshot {fileName}",
                Content = Convert.ToBase64String(buffer.ToArray()),
                Branch = opts.AssetsBranch,
            });

            using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(
                $"/repos/{opts.Owner}/{opts.Repo}/contents/{path}",
                payloadContent,
                ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.TryGetProperty("content", out var contentProp)
                    && contentProp.TryGetProperty("download_url", out var urlProp)
                    && urlProp.GetString() is { Length: > 0 } url)
                {
                    urls.Add(url);
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Failed to upload image {FileName}: {Status} {Error}",
                    fileName, response.StatusCode, error);
            }
        }

        return urls;
    }

    /// <summary>
    /// Restricts a client-supplied file name to characters that are safe in a
    /// repository path (the unique prefix comes from the caller's GUID).
    /// </summary>
    internal static string SanitizeFileName(string fileName)
    {
        var sanitized = new string(fileName
            .Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            .ToArray());
        return string.IsNullOrEmpty(sanitized.Trim('.')) ? "screenshot" : sanitized;
    }

    internal static string BuildIssueBody(
        CreateIssueRequest request,
        List<string> imageUrls,
        int attachedImageCount = 0)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Description");
        sb.AppendLine();
        sb.AppendLine(request.Description);
        sb.AppendLine();

        if (request.Template == "bug")
        {
            if (!string.IsNullOrWhiteSpace(request.StepsToReproduce))
            {
                sb.AppendLine("## Steps to Reproduce");
                sb.AppendLine();
                sb.AppendLine(request.StepsToReproduce);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedBehavior))
            {
                sb.AppendLine($"**Expected:** {request.ExpectedBehavior}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(request.ActualBehavior))
            {
                sb.AppendLine($"**Actual:** {request.ActualBehavior}");
                sb.AppendLine();
            }
        }

        if (request.Template == "data-issue")
        {
            if (!string.IsNullOrWhiteSpace(request.CgmSource))
                sb.AppendLine($"**CGM Source:** {request.CgmSource}");
            if (!string.IsNullOrWhiteSpace(request.TimeRange))
                sb.AppendLine($"**Time Range:** {request.TimeRange}");
            sb.AppendLine();
        }

        if (imageUrls.Count > 0)
        {
            sb.AppendLine("## Screenshots");
            sb.AppendLine();
            foreach (var url in imageUrls)
            {
                sb.AppendLine($"![screenshot]({url})");
                sb.AppendLine();
            }
        }

        if (attachedImageCount > imageUrls.Count)
        {
            var missing = attachedImageCount - imageUrls.Count;
            sb.AppendLine($"*{missing} screenshot(s) were attached but could not be uploaded.*");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Diagnostic Info</summary>");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(request.DiagnosticInfo.Replace("```", "` ` `"));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");

        return sb.ToString();
    }

    private record GitHubCreateIssueRequest
    {
        [JsonPropertyName("title")]
        public string Title { get; init; } = "";
        [JsonPropertyName("body")]
        public string Body { get; init; } = "";
        [JsonPropertyName("labels")]
        public List<string> Labels { get; init; } = [];
    }

    private record GitHubCreateIssueResponse
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";
    }

    private record GitHubCreateContentRequest
    {
        [JsonPropertyName("message")]
        public string Message { get; init; } = "";
        [JsonPropertyName("content")]
        public string Content { get; init; } = "";
        [JsonPropertyName("branch")]
        public string? Branch { get; init; }
    }
}
