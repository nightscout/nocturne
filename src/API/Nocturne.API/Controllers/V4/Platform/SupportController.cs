using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Platform;

[ApiController]
[Authorize]
[Route("api/v4/support")]
public class SupportController(
    GitHubIssueService githubService,
    IOptions<GitHubIssueOptions> options,
    IOptions<OperatorConfiguration> operatorOptions,
    ILogger<SupportController> logger) : ControllerBase
{
    private static readonly HashSet<string> ValidTemplates = ["bug", "feature", "data-issue", "account"];
    private static readonly HashSet<string> AllowedImageTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];
    private const int MaxImages = 4;
    private const long MaxImageBytes = 10 * 1024 * 1024; // 10 MB per image
    private const long MaxTotalBytes = 40 * 1024 * 1024; // 40 MB total

    [HttpPost("issues")]
    [RemoteCommand]
    [EnableRateLimiting("support-issues")]
    [RequestSizeLimit(MaxTotalBytes)]
    [ProducesResponseType(typeof(CreateIssueResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CreateIssueResponse>> CreateIssue(
        [FromForm] string template,
        [FromForm] string title,
        [FromForm] string description,
        [FromForm] string? stepsToReproduce,
        [FromForm] string? expectedBehavior,
        [FromForm] string? actualBehavior,
        [FromForm] string? cgmSource,
        [FromForm] string? timeRange,
        [FromForm] string diagnosticInfo,
        [FromForm] List<IFormFile>? images,
        CancellationToken ct)
    {
        if (!ValidTemplates.Contains(template))
            return Problem(detail: $"Invalid template: {template}", statusCode: 400, title: "Bad Request");

        if (string.IsNullOrWhiteSpace(title) || title.Length > 256)
            return Problem(detail: "Title is required and must be under 256 characters", statusCode: 400, title: "Bad Request");

        if (string.IsNullOrWhiteSpace(description))
            return Problem(detail: "Description is required", statusCode: 400, title: "Bad Request");

        if (string.IsNullOrWhiteSpace(diagnosticInfo))
            return Problem(detail: "Diagnostic info is required", statusCode: 400, title: "Bad Request");

        images ??= [];

        if (images.Count > MaxImages)
            return Problem(detail: $"Maximum {MaxImages} images allowed", statusCode: 400, title: "Bad Request");

        foreach (var image in images)
        {
            if (image.Length > MaxImageBytes)
                return Problem(detail: $"Image {image.FileName} exceeds 10 MB limit", statusCode: 400, title: "Bad Request");
            if (!AllowedImageTypes.Contains(image.ContentType))
                return Problem(detail: $"Image {image.FileName} must be PNG, JPEG, WebP, or GIF", statusCode: 400, title: "Bad Request");
        }

        var request = new CreateIssueRequest
        {
            Template = template,
            Title = title,
            Description = description,
            StepsToReproduce = stepsToReproduce,
            ExpectedBehavior = expectedBehavior,
            ActualBehavior = actualBehavior,
            CgmSource = cgmSource,
            TimeRange = timeRange,
            DiagnosticInfo = diagnosticInfo,
        };

        try
        {
            CreateIssueResponse result;

            if (githubService.HasLocalPat)
            {
                var imageData = images
                    .Select(f => (f.FileName, f.ContentType, (Stream)f.OpenReadStream()))
                    .ToList();

                try
                {
                    result = await githubService.CreateIssueAsync(request, imageData, ct);
                }
                finally
                {
                    foreach (var (_, _, stream) in imageData)
                        await stream.DisposeAsync();
                }
            }
            else
            {
                // Relay to nocturne.run
                using var content = new MultipartFormDataContent();
                content.Add(new StringContent(template), "template");
                content.Add(new StringContent(title), "title");
                content.Add(new StringContent(description), "description");
                if (stepsToReproduce != null) content.Add(new StringContent(stepsToReproduce), "stepsToReproduce");
                if (expectedBehavior != null) content.Add(new StringContent(expectedBehavior), "expectedBehavior");
                if (actualBehavior != null) content.Add(new StringContent(actualBehavior), "actualBehavior");
                if (cgmSource != null) content.Add(new StringContent(cgmSource), "cgmSource");
                if (timeRange != null) content.Add(new StringContent(timeRange), "timeRange");
                content.Add(new StringContent(diagnosticInfo), "diagnosticInfo");

                foreach (var image in images)
                {
                    var streamContent = new StreamContent(image.OpenReadStream());
                    streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);
                    content.Add(streamContent, "images", image.FileName);
                }

                result = await githubService.RelayAsync(content, ct);
            }

            return StatusCode(201, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create GitHub issue");
            return Problem(detail: "Failed to create issue. Try again or report directly on GitHub.",
                statusCode: 502, title: "Bad Gateway");
        }
    }

    /// <summary>
    /// Returns a pre-filled GitHub new-issue URL for fallback when the API is unavailable.
    /// </summary>
    [HttpGet("issues/fallback-url")]
    [RemoteQuery]
    public ActionResult<FallbackUrlResponse> GetFallbackUrl(
        [FromQuery] string template, [FromQuery] string title, [FromQuery] string body)
    {
        var opts = options.Value;
        var label = template switch
        {
            "bug" => "bug",
            "feature" => "enhancement",
            "data-issue" => "data-issue",
            "account" => "account",
            _ => "bug",
        };

        var url = $"https://github.com/{opts.Owner}/{opts.Repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}&labels={Uri.EscapeDataString(label)}";
        return Ok(new FallbackUrlResponse { Url = url });
    }

    /// <summary>
    /// Returns operator support configuration for the frontend.
    /// When no operator is configured, accountBilling is null and the default GitHub flow applies.
    /// </summary>
    [HttpGet("config")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SupportConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<SupportConfigResponse> GetSupportConfig()
    {
        var config = operatorOptions.Value;
        var ab = config.Support.AccountBilling;

        return Ok(new SupportConfigResponse
        {
            AccountBilling = ab is not null && !string.IsNullOrWhiteSpace(ab.Url)
                ? new SupportChannelConfig
                {
                    Mode = ab.Mode == OperatorSupportMode.Redirect ? "redirect" : "api",
                    Url = ab.Url,
                    Label = ab.Label ?? (config.Name is not null ? $"Contact {config.Name}" : null),
                }
                : null,
        });
    }
}
