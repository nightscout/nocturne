using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Profile controller that provides full V3 API compatibility with Nightscout profile endpoints.
/// Implements the /api/v3/profile endpoints with pagination, field selection, sorting, and advanced filtering.
/// </summary>
/// <seealso cref="IProfileProjectionService"/>
/// <seealso cref="IProfileWriteService"/>
/// <seealso cref="Profile"/>
/// <seealso cref="BaseV3Controller{T}"/>
[ApiController]
[Route("api/v3/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class ProfileController : BaseV3Controller<Profile>
{
    private readonly IProfileProjectionService _projectionService;
    private readonly IProfileWriteService _writeService;

    public ProfileController(
        IProfileProjectionService projectionService,
        IProfileWriteService writeService,
        IDocumentProcessingService documentProcessingService,
        ILogger<ProfileController> logger
    )
        : base(documentProcessingService, logger)
    {
        _projectionService = projectionService;
        _writeService = writeService;
    }

    /// <summary>
    /// Get profiles with V3 API features including pagination, field selection, and advanced filtering
    /// </summary>
    /// <returns>V3 profiles collection response</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/profile")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetProfiles(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "V3 profile endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var parameters = ParseV3QueryParameters();

            // Get profiles from projection service
            var profiles = await _projectionService.GetProfilesAsync(
                count: parameters.Limit,
                skip: parameters.Offset,
                ct: cancellationToken
            );

            var profilesList = profiles.ToList();

            // Get total count for pagination
            var totalCount = await _projectionService.CountProfilesAsync(
                ct: cancellationToken
            ); // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(profilesList.Cast<object>());
            var etag = GenerateETag(profilesList);

            if (lastModified.HasValue && ShouldReturn304(etag, lastModified.Value, parameters))
            {
                return StatusCode(304);
            }

            // Create V3 response
            var response = CreateV3CollectionResponse(profilesList, parameters, totalCount);

            _logger.LogDebug(
                "Successfully returned {Count} profiles with V3 format",
                profilesList.Count
            );

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 profile request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 profiles");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get a specific profile by ID with V3 format
    /// </summary>
    /// <param name="id">Profile ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Single profile in V3 format</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v3/profile/{id}")]
    [ProducesResponseType(typeof(Profile), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetProfileById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 profile by ID endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var profile = await _projectionService.GetProfileByIdAsync(id, cancellationToken);

            if (profile == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Profile not found",
                    $"Profile with ID '{id}' was not found"
                );
            }

            var parameters = ParseV3QueryParameters(); // Apply field selection if specified
            var result = ApplyFieldSelection(new[] { profile }, parameters.Fields).FirstOrDefault();

            _logger.LogDebug("Successfully returned profile with ID {Id}", id);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving profile with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create new profiles with V3 format and deduplication support
    /// </summary>
    /// <param name="profileData">Profile data to create (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created profiles</returns>
    [HttpPost]
    [Authorize]
    [NightscoutEndpoint("/api/v3/profile")]
    [ProducesResponseType(typeof(Profile[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> CreateProfile(
        [FromBody] JsonElement profileData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 profile create endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );
        try
        {
            var profiles = ParseCreateRequestFromJsonElement(profileData);

            if (!profiles.Any())
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid profile data"
                );
            }

            // Process each profile (date parsing, validation, etc.)
            foreach (var profile in profiles)
            {
                ProcessProfileForCreation(profile);
            }

            // Create profiles with deduplication support
            var createdProfiles = await _writeService.CreateProfilesAsync(
                profiles,
                cancellationToken
            );

            _logger.LogDebug("Successfully created {Count} profiles", createdProfiles.Count());

            return StatusCode(201, createdProfiles.ToArray());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 profile create request");
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 profiles");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Update a profile by ID with V3 format
    /// </summary>
    /// <param name="id">Profile ID to update</param>
    /// <param name="profile">Updated profile data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated profile</returns>
    [HttpPut("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v3/profile/{id}")]
    [ProducesResponseType(typeof(Profile), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> UpdateProfile(
        string id,
        [FromBody] Profile profile,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 profile update endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (profile == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid profile data"
                );
            }

            ProcessProfileForCreation(profile);

            var updatedProfile = await _writeService.UpdateProfileAsync(
                id,
                profile,
                cancellationToken
            );

            if (updatedProfile == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Profile not found",
                    $"Profile with ID '{id}' was not found"
                );
            }

            _logger.LogDebug("Successfully updated profile with ID {Id}", id);

            return Ok(updatedProfile);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 profile update request for ID {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating profile with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Delete a profile by ID
    /// </summary>
    /// <param name="id">Profile ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v3/profile/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteProfile(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 profile delete endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var deleted = await _writeService.DeleteProfileAsync(id, cancellationToken);

            if (!deleted)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Profile not found",
                    $"Profile with ID '{id}' was not found"
                );
            }

            _logger.LogDebug("Successfully deleted profile with ID {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting profile with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Process profile for creation/update (date parsing, validation, etc.)
    /// Follows the legacy API v3 behavior exactly
    /// </summary>
    /// <param name="profile">Profile to process</param>
    private void ProcessProfileForCreation(Profile profile)
    {
        // Generate identifier if not present (legacy behavior)
        if (string.IsNullOrEmpty(profile.Id))
        {
            profile.Id = GenerateIdentifier(profile);
        }

        // Ensure profile has required properties for V3 compatibility
        if (string.IsNullOrEmpty(profile.CreatedAt))
        {
            profile.CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        // Validate profile structure
        if (profile.Store == null)
        {
            profile.Store = new Dictionary<string, ProfileData>();
        }
    }

    /// <summary>
    /// Generate identifier for profile following legacy API v3 logic
    /// Uses created_at for profile deduplication fallback
    /// </summary>
    /// <param name="profile">Profile record</param>
    /// <returns>Generated identifier</returns>
    private string GenerateIdentifier(Profile profile)
    {
        // Legacy API v3 uses created_at for profile deduplication
        var identifierParts = new List<string>();

        if (
            !string.IsNullOrEmpty(profile.CreatedAt)
            && DateTime.TryParse(profile.CreatedAt, out var parsedDate)
        )
        {
            identifierParts.Add(parsedDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        }

        // Add profile name if available for better identification
        if (!string.IsNullOrEmpty(profile.DefaultProfile))
        {
            identifierParts.Add(profile.DefaultProfile);
        }

        // If we have identifying parts, create a hash-based identifier
        if (identifierParts.Any())
        {
            var combined = string.Join("-", identifierParts);
            return $"profile-{combined.GetHashCode():X}";
        }

        // Fallback to GUID for unique identification
        return Guid.CreateVersion7().ToString();
    }

    /// <summary>
    /// Parse create request from JsonElement for Profile objects
    /// </summary>
    /// <param name="jsonElement">JsonElement containing profile data (single object or array)</param>
    /// <returns>Collection of Profile objects</returns>
    private IEnumerable<Profile> ParseCreateRequestFromJsonElement(JsonElement jsonElement)
    {
        var profileRecords = new List<Profile>();

        try
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in jsonElement.EnumerateArray())
                {
                    var profile = JsonSerializer.Deserialize<Profile>(
                        element.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (profile != null)
                    {
                        profileRecords.Add(profile);
                    }
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.Object)
            {
                var profile = JsonSerializer.Deserialize<Profile>(
                    jsonElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (profile != null)
                {
                    profileRecords.Add(profile);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse profile data from JsonElement");
            throw new ArgumentException("Invalid profile data format", ex);
        }

        return profileRecords;
    }

}
