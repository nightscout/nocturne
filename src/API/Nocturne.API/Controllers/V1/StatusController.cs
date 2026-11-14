using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using IAuthorizationService = Nocturne.Core.Contracts.Identity.IAuthorizationService;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Status controller that provides 1:1 compatibility with Nightscout status endpoint.
/// Content-negotiates like Nightscout: HTML "STATUS OK" unless the Accept header prefers another format.
/// For detailed JSON status, use the V4 status endpoint at /api/v4/status.
/// </summary>
/// <seealso cref="IStatusService"/>
/// <seealso cref="StatusResponse"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
[AllowAnonymous]
public class StatusController : ControllerBase
{
    private const string BadgeUrl = "http://img.shields.io/badge/Nightscout-OK-green.";

    private enum StatusFormat { Html, Png, Svg, Js, Text, Json }

    private static readonly (MediaTypeHeaderValue MediaType, StatusFormat Format)[] FormatsInPreferenceOrder =
    [
        (new("text/html"), StatusFormat.Html),
        (new("image/png"), StatusFormat.Png),
        (new("image/svg+xml"), StatusFormat.Svg),
        (new("application/javascript"), StatusFormat.Js),
        (new("text/plain"), StatusFormat.Text),
        (new("application/json"), StatusFormat.Json),
    ];

    private readonly IStatusService _statusService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<StatusController> _logger;

    /// <summary>
    /// JSON serializer options configured for Nightscout-compatible camelCase responses
    /// without null value serialization.
    /// </summary>
    private static readonly JsonSerializerOptions NightscoutJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of <see cref="StatusController"/>.
    /// </summary>
    /// <param name="statusService">Service providing system status information.</param>
    /// <param name="authorizationService">The access-token exchange that fills <see cref="StatusResponse.Authorized"/>.</param>
    /// <param name="logger">Logger instance.</param>
    public StatusController(
        IStatusService statusService,
        IAuthorizationService authorizationService,
        ILogger<StatusController> logger)
    {
        _statusService = statusService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current system status in the first format the <c>Accept</c> header allows, in
    /// Nightscout's order: HTML, PNG, SVG, JavaScript, plain text, then JSON. A wildcard-only
    /// header therefore gets HTML, and JSON needs <c>application/json</c> (or <c>.json</c>).
    /// A <c>?token=</c> that the access-token exchange accepts puts that exchange's result in
    /// <c>authorized</c>, as Nightscout does; any other token leaves it null.
    /// </summary>
    /// <returns>The status as HTML, plain text, JavaScript or JSON, or a redirect to a badge.</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/status")]
    public async Task<IActionResult> GetStatus()
    {
        _logger.LogDebug(
            "Status endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        Response.Headers.Append(HeaderNames.Vary, "Accept");

        var format = NegotiateFormat();

        switch (format)
        {
            case StatusFormat.Png:
            case StatusFormat.Svg:
                return Redirect(BadgeUrl + (format == StatusFormat.Png ? "png" : "svg"));
            case StatusFormat.Text:
                return Content("STATUS OK", "text/plain");
            case StatusFormat.Html:
                return Content("<h1>STATUS OK</h1>", "text/html");
        }

        try
        {
            var status = await _statusService.GetSystemStatusAsync();
            return RenderStatus(format, await AuthorizeAsync(status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating status response");

            if (format == StatusFormat.Html)
            {
                return Content("<h1>STATUS ERROR</h1>", "text/html");
            }

            return RenderStatus(
                format,
                new StatusResponse
                {
                    Status = "error",
                    Name = "Nocturne",
                    Version = "unknown",
                    ServerTime = DateTime.UtcNow,
                }
            );
        }
    }

    /// <remarks>
    /// The body then differs per token, so it is marked <c>no-store</c> for every cache on the way.
    /// </remarks>
    private async Task<StatusResponse> AuthorizeAsync(StatusResponse status)
    {
        var token = Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            return status;
        }

        Response.Headers.CacheControl = "no-store";
        var authorized = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);
        return authorized is null ? status : status.WithAuthorized(authorized);
    }

    private IActionResult RenderStatus(StatusFormat format, StatusResponse status)
    {
        if (format == StatusFormat.Js)
        {
            var json = JsonSerializer.Serialize(status, NightscoutJsonOptions);
            return Content($"this.serverSettings = {json};", "application/javascript");
        }

        return new JsonResult(status, NightscoutJsonOptions) { ContentType = "application/json" };
    }

    private StatusFormat NegotiateFormat()
    {
        var ranges = Request.GetTypedHeaders().Accept;
        if (ranges.Count == 0)
        {
            return StatusFormat.Html;
        }

        var accepted = new List<(double Quality, int Specificity, int RangeIndex, int FormatIndex, StatusFormat Format)>();
        for (var formatIndex = 0; formatIndex < FormatsInPreferenceOrder.Length; formatIndex++)
        {
            var (mediaType, format) = FormatsInPreferenceOrder[formatIndex];
            (double Quality, int Specificity, int RangeIndex)? deciding = null;

            for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                var range = ranges[rangeIndex];
                if (!mediaType.IsSubsetOf(range))
                {
                    continue;
                }

                var candidate = (range.Quality ?? 1.0, Specificity(range), rangeIndex);
                if (deciding is null
                    || candidate.Item2 > deciding.Value.Specificity
                    || (candidate.Item2 == deciding.Value.Specificity && candidate.Item1 > deciding.Value.Quality))
                {
                    deciding = candidate;
                }
            }

            if (deciding is { Quality: > 0 } found)
            {
                accepted.Add((found.Quality, found.Specificity, found.RangeIndex, formatIndex, format));
            }
        }

        return accepted
            .OrderByDescending(a => a.Quality)
            .ThenByDescending(a => a.Specificity)
            .ThenBy(a => a.RangeIndex)
            .ThenBy(a => a.FormatIndex)
            .Select(a => a.Format)
            .FirstOrDefault(StatusFormat.Html);
    }

    private static int Specificity(MediaTypeHeaderValue range) =>
        range.MatchesAllTypes ? 0 : range.MatchesAllSubTypes ? 1 : 2;
}
