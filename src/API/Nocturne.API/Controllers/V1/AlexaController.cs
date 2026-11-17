using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Alexa controller that provides voice assistant integration.
/// Maintains 1:1 compatibility with legacy Nightscout Alexa API.
/// </summary>
/// <seealso cref="IAlexaService"/>
/// <seealso cref="IAuthorizationService"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
public class AlexaController : ControllerBase
{
    /// <summary>
    /// What every request needs to be served at all. Answers that read more check their own scopes in
    /// <see cref="IAlexaService"/>, so a credential limited to glucose still gets glucose answers.
    /// </summary>
    private static readonly string[] RequiredReadScopes = [Scope.GlucoseRead];

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
    /// Maintains complete compatibility with the legacy /api/v1/alexa endpoint
    /// </summary>
    /// <param name="request">Alexa Skills Kit request from Amazon</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Alexa response with speech output and session handling</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v1/alexa")]
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
            }
            if (!RequiredReadScopes.All(HttpContext.HasScope))
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

            return response == null ? new JsonResult(string.Empty) : Ok(response);
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
            return Ok(_alexaService.BuildErrorResponse());
        }
    }
}
