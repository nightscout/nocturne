using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Alexa controller that provides voice assistant integration.
/// Maintains 1:1 compatibility with legacy Nightscout Alexa API.
/// </summary>
/// <seealso cref="IAlexaService"/>
/// <seealso cref="IAuthorizationService"/>
[ApiController]
[Tags("V1")]
[Route("api/[controller]")]
public class AlexaController : ControllerBase
{
    private readonly IAlexaService _alexaService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<AlexaController> _logger;

    public AlexaController(
        IAlexaService alexaService,
        IAuthorizationService authorizationService,
        ILogger<AlexaController> logger
    )
    {
        _alexaService = alexaService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    /// <summary>
    /// Handle Alexa Skills Kit requests for voice assistant integration
    /// Processes LaunchRequest, IntentRequest, and SessionEndedRequest types
    /// Maintains complete compatibility with legacy /api/alexa endpoint
    /// </summary>
    /// <param name="request">Alexa Skills Kit request from Amazon</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Alexa response with speech output and session handling</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/alexa")]
    [ProducesResponseType(typeof(AlexaResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<AlexaResponse>> HandleAlexaRequest(
        [FromBody] AlexaRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Incoming request from Alexa");

        try
        {
            // Validate request
            if (request?.Request == null)
            {
                _logger.LogWarning("Invalid Alexa request received - missing request details");
                return BadRequest("Invalid Alexa request format");
            } // Check authorization - requires read permission as per legacy implementation
            if (!await _authorizationService.CheckPermissionAsync("api", "api:*:read"))
            {
                _logger.LogWarning(
                    "Unauthorized Alexa request from {RemoteIpAddress}",
                    HttpContext.Connection.RemoteIpAddress
                );
                return Unauthorized("Access denied");
            }

            // Log locale information as per legacy implementation
            var locale = request.Request.Locale;
            if (!string.IsNullOrEmpty(locale))
            {
                _logger.LogDebug("Alexa request locale: {Locale}", locale);
            }

            // Process the request
            var response = await _alexaService.ProcessRequestAsync(request, cancellationToken);

            _logger.LogDebug(
                "Successfully processed Alexa {RequestType} request",
                request.Request.Type
            );

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Invalid Alexa request format");
            return BadRequest(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Unauthorized Alexa request");
            return Unauthorized(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Alexa request");

            // Return a valid Alexa error response instead of HTTP error to maintain skill functionality
            var errorResponse = _alexaService.BuildSpeechletResponse(
                "Error",
                "Sorry, I'm having trouble right now. Please try again later.",
                string.Empty,
                true
            );

            return Ok(errorResponse);
        }
    }
}
