using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Services;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>How many screenshots a support submission may carry, and how large.</summary>
internal sealed record SupportImageLimits(int MaxCount, long MaxBytesEach, long MaxTotalBytes);

[ApiController]
[Authorize]
[Route("api/v4/support")]
public class SupportController(
    GitHubIssueService githubService,
    ISupportDiagnosticsService diagnosticsService,
    IOptions<GitHubIssueOptions> options,
    IOptions<OperatorConfiguration> operatorOptions,
    ILogger<SupportController> logger) : ControllerBase
{
    private static readonly HashSet<string> ValidTemplates = ["bug", "feature", "data-issue", "account"];
    private static readonly HashSet<string> AllowedImageTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];
    private const int MaxTitleLength = 256;
    private const long MaxTotalBytes = 40 * 1024 * 1024;

    private const long MaxRelayTotalBytes = 10 * 1024 * 1024;

    // Multipart framing and the text fields ride on top of the images.
    private const long MaxRelayRequestBytes = MaxRelayTotalBytes + 1024 * 1024;

    internal static readonly SupportImageLimits DirectImageLimits = new(4, 10 * 1024 * 1024, MaxTotalBytes);

    // The relay is anonymous and every image it accepts is a commit to the assets branch on the
    // operator's PAT, so it takes half the bytes per image and a quarter in total. The count
    // matches the form's, so a reporter on a relaying instance loses only headroom; 5 MB still
    // admits a full-resolution phone or desktop screenshot.
    internal static readonly SupportImageLimits RelayImageLimits = new(4, 5 * 1024 * 1024, MaxRelayTotalBytes);

    /// <remarks>
    /// Validated against <see cref="RelayImageLimits"/> when this instance will relay, so a
    /// submission the relay would refuse is refused here instead of after the upload.
    /// </remarks>
    // No [RemoteCommand]: command arguments are devalue-serialised and cannot
    // carry File objects. The frontend submits through the hand-maintained
    // form remote in support.remote.ts, which models the multipart upload.
    [HttpPost("issues")]
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
        images ??= [];

        var relay = !githubService.HasLocalPat;
        var validationError = await ValidateAsync(
            request, images, relay ? RelayImageLimits : DirectImageLimits, ct);
        if (validationError is not null)
            return validationError;

        return relay
            ? await SubmitAsync(() => RelayAsync(request, images, ct), "support issue (relayed)")
            : await SubmitAsync(() => CreateLocallyAsync(request, images, ct), "support issue");
    }

    /// <summary>
    /// Anonymous ingress for issues relayed from instances without their own PAT (the
    /// nocturne.run side of the relay). The relayed payload is re-validated here, against the
    /// lower <see cref="RelayImageLimits"/>; the rate limit is shared with the authenticated
    /// endpoint.
    /// </summary>
    [HttpPost("relay")]
    [AllowAnonymous]
    [EnableRateLimiting("support-issues")]
    [RequestSizeLimit(MaxRelayRequestBytes)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult<CreateIssueResponse>> AcceptRelayedIssue(
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
        if (!githubService.AcceptsRelay)
            return NotFound();

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
        images ??= [];

        var validationError = await ValidateAsync(request, images, RelayImageLimits, ct);
        if (validationError is not null)
            return validationError;

        return await SubmitAsync(() => CreateLocallyAsync(request, images, ct), "relayed support issue");
    }

    /// <summary>
    /// The one validation both ingresses run, so a relayed submission is held to everything a
    /// direct one is.
    /// </summary>
    internal async Task<ObjectResult?> ValidateAsync(
        CreateIssueRequest request,
        IReadOnlyList<IFormFile> images,
        SupportImageLimits limits,
        CancellationToken ct)
    {
        if (request.Template is null || !ValidTemplates.Contains(request.Template))
            return Invalid($"Invalid template: {request.Template}");

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
            return Invalid($"Title is required and must be under {MaxTitleLength} characters");

        if (string.IsNullOrWhiteSpace(request.Description))
            return Invalid("Description is required");

        if (string.IsNullOrWhiteSpace(request.DiagnosticInfo))
            return Invalid("Diagnostic info is required");

        if (images.Count > limits.MaxCount)
            return Invalid($"Maximum {limits.MaxCount} images allowed");

        foreach (var image in images)
        {
            if (image.Length > limits.MaxBytesEach)
                return Invalid($"Image {image.FileName} exceeds {limits.MaxBytesEach / (1024 * 1024)} MB limit");
            if (!AllowedImageTypes.Contains(image.ContentType)
                || !await HasSignatureOfAsync(image, ct))
                return Invalid($"Image {image.FileName} must be PNG, JPEG, WebP, or GIF");
        }

        if (images.Sum(i => i.Length) > limits.MaxTotalBytes)
            return Invalid($"Images exceed {limits.MaxTotalBytes / (1024 * 1024)} MB in total");

        return null;
    }

    /// <summary>
    /// Whether the file's leading bytes are those of its declared image type. The content type is
    /// the caller's claim, and whatever passes is committed to a public repository and served from
    /// raw.githubusercontent.com.
    /// </summary>
    private static async Task<bool> HasSignatureOfAsync(IFormFile image, CancellationToken ct)
    {
        var header = new byte[12];
        await using var stream = image.OpenReadStream();
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var bytes = header.AsSpan(0, read);

        return image.ContentType switch
        {
            "image/png" => bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "image/jpeg" => bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "image/webp" => read == 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..].SequenceEqual("WEBP"u8),
            _ => false,
        };
    }

    private ObjectResult Invalid(string detail) =>
        Problem(detail: detail, statusCode: 400, title: "Bad Request");

    private async Task<CreateIssueResponse> CreateLocallyAsync(
        CreateIssueRequest request, IReadOnlyList<IFormFile> images, CancellationToken ct)
    {
        var imageData = images
            .Select(f => (f.FileName, f.ContentType, (Stream)f.OpenReadStream()))
            .ToList();

        try
        {
            return await githubService.CreateIssueAsync(request, imageData, ct);
        }
        finally
        {
            foreach (var (_, _, stream) in imageData)
                await stream.DisposeAsync();
        }
    }

    private async Task<CreateIssueResponse> RelayAsync(
        CreateIssueRequest request, IReadOnlyList<IFormFile> images, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(request.Template), "template");
        content.Add(new StringContent(request.Title), "title");
        content.Add(new StringContent(request.Description), "description");
        if (request.StepsToReproduce != null) content.Add(new StringContent(request.StepsToReproduce), "stepsToReproduce");
        if (request.ExpectedBehavior != null) content.Add(new StringContent(request.ExpectedBehavior), "expectedBehavior");
        if (request.ActualBehavior != null) content.Add(new StringContent(request.ActualBehavior), "actualBehavior");
        if (request.CgmSource != null) content.Add(new StringContent(request.CgmSource), "cgmSource");
        if (request.TimeRange != null) content.Add(new StringContent(request.TimeRange), "timeRange");
        content.Add(new StringContent(request.DiagnosticInfo), "diagnosticInfo");

        foreach (var image in images)
        {
            var streamContent = new StreamContent(image.OpenReadStream());
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);
            content.Add(streamContent, "images", image.FileName);
        }

        return await githubService.RelayAsync(content, ct);
    }

    /// <summary>
    /// One error policy for both ingresses. The 502 is what sends the form to its fallback
    /// (a pre-filled GitHub link), so every upstream failure has to surface as one.
    /// </summary>
    private async Task<ActionResult<CreateIssueResponse>> SubmitAsync(
        Func<Task<CreateIssueResponse>> submit, string logContext)
    {
        try
        {
            return StatusCode(201, await submit());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create {LogContext}", logContext);
            return Problem(detail: "Failed to create issue. Try again or report directly on GitHub.",
                statusCode: 502, title: "Bad Gateway");
        }
    }

    /// <summary>
    /// Returns the tenant configuration a reporter may attach to a support issue.
    /// </summary>
    /// <remarks>
    /// Gated like <see cref="V4.ConnectorStatusController"/>, whose connector health this reports a
    /// subset of: reaching it with a narrower credential than that endpoint requires would make
    /// support the way to read connector state without <c>tenant_settings</c>.
    /// </remarks>
    [HttpGet("diagnostics")]
    [RemoteQuery]
    [DenyDemoSubject]
    [RequireScope(Scope.TenantSettings)]
    [ProducesResponseType(typeof(SupportDiagnosticsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SupportDiagnosticsResponse>> GetSupportDiagnostics(
        CancellationToken ct)
    {
        return Ok(await diagnosticsService.GetAsync(ct));
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
    /// When no operator is configured, both channels are null and the default GitHub flow applies.
    /// </summary>
    /// <remarks>
    /// Anonymous because the hosts that most need the operator's address — an inactive tenant's,
    /// and the apex with no tenant — are the ones no session can be established on.
    /// </remarks>
    [HttpGet("config")]
    [AllowAnonymous]
    [RemoteQuery]
    [ProducesResponseType(typeof(SupportConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<SupportConfigResponse> GetSupportConfig()
    {
        var config = operatorOptions.Value;
        var ab = config.Support.AccountBilling;
        var portal = config.Support.AccountPortal;
        var operatorLabel = config.Name is not null ? $"Contact {config.Name}" : null;

        return Ok(new SupportConfigResponse
        {
            AccountBilling = ab is not null && !string.IsNullOrWhiteSpace(ab.Url)
                ? new SupportChannelConfig
                {
                    Mode = ab.Mode == OperatorSupportMode.Redirect ? "redirect" : "api",
                    Url = ab.Url,
                    Label = ab.Label ?? operatorLabel,
                }
                : null,
            AccountPortal = portal is not null && !string.IsNullOrWhiteSpace(portal.Url)
                ? new AccountPortalConfig
                {
                    Url = portal.Url,
                    Label = portal.Label ?? operatorLabel,
                }
                : null,
        });
    }
}
