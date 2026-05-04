using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Nocturne.API.Services;

public class GitHubIssueOptions
{
    public string? IssuesPat { get; set; }
    public string RelayUrl { get; set; } = "https://nocturne.run/api/v4/support/issues";
    public string Owner { get; set; } = "nightscout";
    public string Repo { get; set; } = "nocturne";
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
        var body = BuildIssueBody(request, imageUrls);
        var label = TemplateLabels.GetValueOrDefault(request.Template, "bug");

        using var client = CreateGitHubClient();
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

    private async Task<List<string>> UploadImagesAsync(
        IReadOnlyList<(string FileName, string ContentType, Stream Content)> images,
        CancellationToken ct)
    {
        var urls = new List<string>();
        if (images.Count == 0) return urls;

        using var client = CreateGitHubClient();
        var opts = options.Value;

        foreach (var (fileName, contentType, imageContent) in images)
        {
            var uploadContent = new MultipartFormDataContent();
            var streamContent = new StreamContent(imageContent);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            uploadContent.Add(streamContent, "file", fileName);

            var uploadUrl = $"https://uploads.github.com/repos/{opts.Owner}/{opts.Repo}/upload/assets";
            var response = await client.PostAsync(uploadUrl, uploadContent, ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.TryGetProperty("browser_download_url", out var urlProp))
                {
                    urls.Add(urlProp.GetString() ?? "");
                }
            }
            else
            {
                logger.LogWarning("Failed to upload image {FileName}: {Status}",
                    fileName, response.StatusCode);
            }
        }

        return urls;
    }

    internal static string BuildIssueBody(CreateIssueRequest request, List<string> imageUrls)
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

    private HttpClient CreateGitHubClient()
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Nocturne", "1.0"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.IssuesPat);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
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
}
