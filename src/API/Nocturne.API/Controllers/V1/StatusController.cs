using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Status controller that provides 1:1 compatibility with Nightscout status endpoint.
/// Returns HTML "STATUS OK" by default (matching Nightscout), or JSON when requested via Accept header.
/// For detailed JSON status, use the V4 status endpoint at /api/v4/status
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class StatusController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly ILogger<StatusController> _logger;

    /// <summary>
    /// JSON serializer options for Nightscout-compatible responses
    /// </summary>
    private static readonly JsonSerializerOptions NightscoutJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public StatusController(IStatusService statusService, ILogger<StatusController> logger)
    {
        _statusService = statusService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current system status as JSON.
    /// This is the .json suffix variant that always returns JSON (Nightscout compatibility).
    /// </summary>
    /// <returns>Status response in JSON format</returns>
    [HttpGet("~/api/v1/status.json")]
    [NightscoutEndpoint("/api/v1/status.json")]
    [Produces("application/json")]
    public async Task<IActionResult> GetStatusJson()
    {
        _logger.LogDebug(
            "Status.json endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var status = await _statusService.GetSystemStatusAsync();
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating status.json response");
            return Ok(
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

    /// <summary>
    /// Get the current system status.
    /// Returns JSON when Accept header includes application/json (Nightscout client behavior),
    /// otherwise returns HTML for browser access.
    /// </summary>
    /// <returns>Status response in HTML or JSON format</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/status")]
    public async Task<IActionResult> GetStatus()
    {
        _logger.LogDebug(
            "Status endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            // Check Accept header to determine response format
            // Nightscout clients send Accept: application/json and expect JSON response
            var acceptHeader = Request.Headers.Accept.ToString().ToLowerInvariant();
            var wantsJson = acceptHeader.Contains("application/json") ||
                            acceptHeader.Contains("*/*");

            if (wantsJson)
            {
                // Return full JSON status for clients that request it
                var status = await _statusService.GetSystemStatusAsync();
                return new JsonResult(status, NightscoutJsonOptions) { ContentType = "application/json" };
            }

            // Default: Return simple HTML "STATUS OK" for browser access
            return Content("<h1>STATUS OK</h1>", "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating status response");

            // Return error status
            var acceptHeader = Request.Headers.Accept.ToString().ToLowerInvariant();
            if (acceptHeader.Contains("application/json") || acceptHeader.Contains("*/*"))
            {
                return new JsonResult(
                    new StatusResponse
                    {
                        Status = "error",
                        Name = "Nocturne",
                        Version = "unknown",
                        ServerTime = DateTime.UtcNow,
                    },
                    NightscoutJsonOptions
                ) { ContentType = "application/json" };
            }

            return Content("<h1>STATUS ERROR</h1>", "text/html");
        }
    }
}
