using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// V2 Properties controller providing client properties and settings endpoints
/// Implements the legacy /api/v2/properties endpoints with 1:1 backwards compatibility
/// </summary>
[ApiController]
[Route("api/v2/properties")]
[Produces("application/json")]
[Tags("V2 Properties")]
[ClientPropertyName("v2Properties")]
public class PropertiesController : ControllerBase
{
    private readonly IPropertiesService _propertiesService;
    private readonly ILogger<PropertiesController> _logger;

    public PropertiesController(
        IPropertiesService propertiesService,
        ILogger<PropertiesController> logger
    )
    {
        _propertiesService = propertiesService;
        _logger = logger;
    }

    /// <summary>
    /// Get all client properties and settings
    /// Returns comprehensive properties structure containing plugin data, settings, and computed values
    /// </summary>
    /// <param name="pretty">Format JSON with indentation for readability</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete properties structure for current state</returns>
    /// <response code="200">Returns the properties structure</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet]
    [ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Dictionary<string, object>>> GetAllProperties(
        [FromQuery] bool pretty = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var properties = await _propertiesService.GetAllPropertiesAsync(cancellationToken);

            if (pretty)
            {
                var prettyJson = JsonSerializer.Serialize(
                    properties,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                return Content(prettyJson, "application/json");
            }

            return Ok(properties);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all properties");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get specific properties by names (comma-separated)
    /// Supports paths like /prop1 or /prop1,prop3 to get only specific properties
    /// </summary>
    /// <param name="propertyPath">Comma-separated list of property names to retrieve</param>
    /// <param name="pretty">Format JSON with indentation for readability</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Selected properties</returns>
    /// <response code="200">Returns the selected properties</response>
    /// <response code="400">If property names are invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("{*propertyPath}")]
    [ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Dictionary<string, object>>> GetSpecificProperties(
        string propertyPath,
        [FromQuery] bool pretty = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                return BadRequest(new { error = "Property path cannot be empty" });
            }

            // Split the path by commas and filter out empty segments
            var segments = propertyPath
                .Split('/')
                .Where(segment => !string.IsNullOrEmpty(segment))
                .ToList();

            if (!segments.Any())
            {
                return BadRequest(new { error = "No valid property names provided" });
            }

            // If there's only one segment, split by comma for multiple properties
            var propertyNames =
                segments.Count == 1
                    ? segments[0].Split(',').Where(name => !string.IsNullOrWhiteSpace(name))
                    : segments;

            var properties = await _propertiesService.GetPropertiesAsync(
                propertyNames,
                cancellationToken
            );

            if (pretty)
            {
                var prettyJson = JsonSerializer.Serialize(
                    properties,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                return Content(prettyJson, "application/json");
            }

            return Ok(properties);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error getting specific properties for path: {PropertyPath}",
                propertyPath
            );
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
