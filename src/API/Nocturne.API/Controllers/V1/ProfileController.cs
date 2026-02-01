using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Profile controller that provides 1:1 compatibility with Nightscout profile endpoints
/// Implements the /api/v1/profile/* endpoints from the legacy JavaScript implementation
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class ProfileController : ControllerBase
{
    private readonly IProfileDataService _profileDataService;
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        IProfileDataService profileDataService,
        ILogger<ProfileController> logger
    )
    {
        _profileDataService = profileDataService;
        _logger = logger;
    }

    /// <summary>
    /// Get profiles with optional pagination
    /// </summary>
    /// <param name="count">Maximum number of profiles to return (default: 10)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of profiles</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/profile")]
    [ProducesResponseType(typeof(Profile[]), 200)]
    [ProducesResponseType(typeof(Profile[]), 304)] // Not Modified response
    public async Task<ActionResult<Profile[]>> GetProfiles(
        [FromQuery] int count = 10,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Profile GET endpoint requested with count: {Count} from {RemoteIpAddress}",
            count,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Limit count to reasonable maximum to prevent abuse
            count = Math.Max(1, Math.Min(count, 1000));
            var profiles = await _profileDataService.GetProfilesAsync(
                count: count,
                skip: 0,
                cancellationToken: cancellationToken
            );
            var profilesArray = profiles.ToArray();

            // Set Last-Modified header for caching
            DateTimeOffset lastModified;
            if (profilesArray.Length > 0)
            {
                // Set Last-Modified header based on most recent profile
                lastModified = DateTimeOffset.FromUnixTimeMilliseconds(profilesArray[0].Mills);
            }
            else
            {
                lastModified = DateTimeOffset.UtcNow.AddDays(-1); // Default fallback
            }

            Response.Headers.LastModified = lastModified.ToString("R");

            // Check If-Modified-Since header for conditional requests
            if (Request.Headers.IfModifiedSince.Count > 0)
            {
                if (
                    DateTimeOffset.TryParse(
                        Request.Headers.IfModifiedSince.ToString(),
                        out var ifModifiedSince
                    )
                )
                {
                    if (lastModified <= ifModifiedSince)
                    {
                        _logger.LogDebug("Returning 304 Not Modified for profiles request");
                        return StatusCode(304, Array.Empty<Profile>());
                    }
                }
            }

            _logger.LogDebug("Returning {Count} profiles", profilesArray.Length);
            return Ok(profilesArray);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client cancelled the request (navigated away, closed tab, etc.)
            // This is normal behavior, not an error
            _logger.LogDebug("Profile request was cancelled by the client");
            return StatusCode(499, Array.Empty<Profile>()); // 499 = Client Closed Request (nginx convention)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching profiles");
            return StatusCode(500, Array.Empty<Profile>());
        }
    }

    /// <summary>
    /// Create or update a profile.
    /// Nightscout accepts either a single profile object or an array of profiles.
    /// </summary>
    /// <param name="body">Profile(s) to create or update (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created profiles with assigned IDs as an array</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v1/profile")]
    [ProducesResponseType(typeof(Profile[]), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<Profile[]>> CreateProfiles(
        [FromBody] System.Text.Json.JsonElement body,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Profile POST endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            Profile[] profiles;

            // Handle both single profile and array of profiles (Nightscout compatibility)
            if (body.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                profiles = System.Text.Json.JsonSerializer.Deserialize<Profile[]>(
                    body.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? Array.Empty<Profile>();
            }
            else if (body.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(
                    body.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                profiles = profile != null ? new[] { profile } : Array.Empty<Profile>();
            }
            else
            {
                _logger.LogDebug("Invalid profile data format");
                return BadRequest("Invalid profile data format");
            }

            _logger.LogDebug("Processing {Count} profiles", profiles.Length);

            if (profiles.Length == 0)
            {
                _logger.LogDebug("No profiles provided in request body");
                return Ok(Array.Empty<Profile>());
            }

            var createdProfiles = await _profileDataService.CreateProfilesAsync(
                profiles,
                cancellationToken
            );
            var result = createdProfiles.ToArray();

            _logger.LogDebug("Created {Count} profiles", result.Length);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating profiles");
            return StatusCode(500, Array.Empty<Profile>());
        }
    }

    /// <summary>
    /// Get the current active profile
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The current active profile as a single object (Nightscout format), or empty array if no profiles exist</returns>
    [HttpGet("current")]
    [NightscoutEndpoint("/api/v1/profile/current")]
    [ProducesResponseType(typeof(Profile), 200)]
    [ProducesResponseType(typeof(Profile[]), 200)] // Empty array when no profile
    [ProducesResponseType(typeof(Profile[]), 304)] // Not Modified response
    public async Task<ActionResult> GetCurrentProfile(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Profile current endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var profile = await _profileDataService.GetCurrentProfileAsync(cancellationToken);

            if (profile == null)
            {
                _logger.LogDebug("No current profile found, returning empty array");
                return Ok(Array.Empty<Profile>());
            }

            // Set Last-Modified header for caching
            var lastModified = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills);
            Response.Headers.LastModified = lastModified.ToString("R");

            // Check If-Modified-Since header for conditional requests
            if (Request.Headers.IfModifiedSince.Count > 0)
            {
                if (
                    DateTimeOffset.TryParse(
                        Request.Headers.IfModifiedSince.ToString(),
                        out var ifModifiedSince
                    )
                )
                {
                    if (lastModified <= ifModifiedSince)
                    {
                        _logger.LogDebug("Returning 304 Not Modified for current profile request");
                        return StatusCode(304, Array.Empty<Profile>());
                    }
                }
            }

            _logger.LogDebug("Returning current profile with ID: {ProfileId}", profile.Id);
            // Nightscout returns a single object, not an array
            return Ok(profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching current profile");
            return StatusCode(500, Array.Empty<Profile>());
        }
    }

    /// <summary>
    /// Get a specific profile by ID or treat the spec as a profile ID
    /// </summary>
    /// <param name="spec">The profile ID (24-character hex string for MongoDB ObjectId)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The profile with the specified ID, or empty array if not found</returns>
    [HttpGet("{spec}")]
    [NightscoutEndpoint("/api/v1/profile/{spec}")]
    [ProducesResponseType(typeof(Profile[]), 200)]
    [ProducesResponseType(typeof(Profile[]), 304)] // Not Modified response
    public async Task<ActionResult<Profile[]>> GetProfile(
        string spec,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Profile spec endpoint requested with spec: {Spec} from {RemoteIpAddress}",
            spec,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Check if spec is a 24-character hex string (MongoDB ObjectId)
            bool isId =
                spec.Length == 24
                && System.Text.RegularExpressions.Regex.IsMatch(
                    spec,
                    "^[a-f\\d]{24}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

            if (isId)
            {
                // Fetch specific profile by ID
                var profile = await _profileDataService.GetProfileByIdAsync(
                    spec,
                    cancellationToken
                );

                if (profile == null)
                {
                    _logger.LogDebug("Profile with ID {ProfileId} not found", spec);
                    return Ok(Array.Empty<Profile>());
                }

                // Set Last-Modified header for caching
                var lastModified = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills);
                Response.Headers.LastModified = lastModified.ToString("R");

                // Check If-Modified-Since header for conditional requests
                if (Request.Headers.IfModifiedSince.Count > 0)
                {
                    if (
                        DateTimeOffset.TryParse(
                            Request.Headers.IfModifiedSince.ToString(),
                            out var ifModifiedSince
                        )
                    )
                    {
                        if (lastModified <= ifModifiedSince)
                        {
                            _logger.LogDebug(
                                "Returning 304 Not Modified for profile ID {ProfileId}",
                                spec
                            );
                            return StatusCode(304, Array.Empty<Profile>());
                        }
                    }
                }

                _logger.LogDebug("Returning profile with ID: {ProfileId}", spec);
                return Ok(new[] { profile });
            }
            else
            {
                // For non-ObjectId specs, return empty array (consistent with Nightscout behavior)
                _logger.LogDebug("Spec {Spec} is not a valid MongoDB ObjectId", spec);
                return Ok(Array.Empty<Profile>());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching profile with spec: {Spec}", spec);
            return StatusCode(500, Array.Empty<Profile>());
        }
    }
}
