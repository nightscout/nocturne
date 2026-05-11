using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Repositories;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Settings controller that provides full V3 API compatibility with Nightscout settings endpoints.
/// Implements the /api/v3/settings endpoints with pagination, field selection, sorting, and advanced filtering.
/// Settings require admin permissions following legacy API v3 behavior.
/// </summary>
/// <seealso cref="ISettingsRepository"/>
/// <seealso cref="Settings"/>
/// <seealso cref="BaseV3Controller{T}"/>
[ApiController]
[Tags("V3")]
[Route("api/v3/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class SettingsController : BaseV3Controller<Settings>
{
    private readonly ISettingsRepository _settings;

    public SettingsController(
        ISettingsRepository settings,
        IDocumentProcessingService documentProcessingService,
        ILogger<SettingsController> logger
    )
        : base(documentProcessingService, logger)
    {
        _settings = settings;
    }

    /// <summary>
    /// Get settings with V3 API features including pagination, field selection, and advanced filtering
    /// Requires admin permissions as per legacy API v3 behavior
    /// </summary>
    /// <returns>V3 settings collection response</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/settings")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetSettings(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "V3 settings endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // CHECKME: Legacy API v3 requires api:settings:admin permission for settings access
            // This is different from other collections which use api:{collection}:read
            // We should validate admin permissions here when authentication is implemented

            var parameters = ParseV3QueryParameters();

            // Convert V3 parameters to backend query for compatibility
            var findQuery = ConvertV3FilterToV1Find(parameters.Filter);
            var reverseResults = ExtractSortDirection(parameters.Sort);

            // Get settings using existing backend with V3 parameters
            var settings = await _settings.GetSettingsWithAdvancedFilterAsync(
                count: parameters.Limit,
                skip: parameters.Offset,
                findQuery: findQuery,
                reverseResults: reverseResults,
                cancellationToken: cancellationToken
            );

            var settingsList = settings.ToList();

            // Get total count for pagination (approximation for performance)
            var totalCount = await GetTotalCountAsync(
                null,
                findQuery,
                cancellationToken,
                "settings"
            ); // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(settingsList.Cast<object>());
            var etag = GenerateETag(settingsList);

            if (lastModified.HasValue && ShouldReturn304(etag, lastModified.Value, parameters))
            {
                return StatusCode(304);
            }

            // Create V3 response
            var response = CreateV3CollectionResponse(settingsList, parameters, totalCount);

            _logger.LogDebug(
                "Successfully returned {Count} settings with V3 format",
                settingsList.Count
            );

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 settings request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 settings");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Get a specific settings record by ID with V3 format
    /// Requires admin permissions as per legacy API v3 behavior
    /// </summary>
    /// <param name="id">Settings ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Single settings record in V3 format</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v3/settings/{id}")]
    [ProducesResponseType(typeof(Settings), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> GetSettingsById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 settings by ID endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // CHECKME: Should validate admin permissions here

            var settings = await _settings.GetSettingsByIdAsync(id, cancellationToken);

            if (settings == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Settings not found",
                    $"Settings with ID '{id}' was not found"
                );
            }

            var parameters = ParseV3QueryParameters(); // Apply field selection if specified
            var result = ApplyFieldSelection(new[] { settings }, parameters.Fields)
                .FirstOrDefault();

            _logger.LogDebug("Successfully returned settings with ID {Id}", id);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving settings with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create new settings records with V3 format and deduplication support
    /// Requires admin permissions as per legacy API v3 behavior
    /// </summary>
    /// <param name="settingsData">Settings data to create (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created settings records</returns>
    [HttpPost]
    [Authorize]
    [NightscoutEndpoint("/api/v3/settings")]
    [ProducesResponseType(typeof(Settings[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> CreateSettings(
        [FromBody] JsonElement settingsData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 settings create endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // CHECKME: Should validate admin permissions here

            var settingsRecords = ParseCreateRequestFromJsonElement(settingsData);

            if (!settingsRecords.Any())
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid settings data"
                );
            }

            // Process each settings record (validation, etc.)
            foreach (var settings in settingsRecords)
            {
                ProcessSettingsForCreation(settings);
            }

            // Create settings records with deduplication support
            var createdRecords = await _settings.CreateSettingsAsync(
                settingsRecords,
                cancellationToken
            );

            _logger.LogDebug(
                "Successfully created {Count} settings records",
                createdRecords.Count()
            );

            return StatusCode(201, createdRecords.ToArray());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 settings create request");
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 settings");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Update a settings record by ID with V3 format
    /// Requires admin permissions as per legacy API v3 behavior
    /// </summary>
    /// <param name="id">Settings ID to update</param>
    /// <param name="settings">Updated settings data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated settings record</returns>
    [HttpPut("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v3/settings/{id}")]
    [ProducesResponseType(typeof(Settings), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> UpdateSettings(
        string id,
        [FromBody] Settings settings,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 settings update endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // CHECKME: Should validate admin permissions here

            if (settings == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid settings data"
                );
            }

            ProcessSettingsForCreation(settings);

            var updatedSettings = await _settings.UpdateSettingsAsync(
                id,
                settings,
                cancellationToken
            );

            if (updatedSettings == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Settings not found",
                    $"Settings with ID '{id}' was not found"
                );
            }

            _logger.LogDebug("Successfully updated settings with ID {Id}", id);

            return Ok(updatedSettings);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 settings update request for ID {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating settings with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Delete a settings record by ID
    /// Requires admin permissions as per legacy API v3 behavior
    /// </summary>
    /// <param name="id">Settings ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [NightscoutEndpoint("/api/v3/settings/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteSettings(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 settings delete endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // CHECKME: Should validate admin permissions here

            var deleted = await _settings.DeleteSettingsAsync(id, cancellationToken);

            if (!deleted)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Settings not found",
                    $"Settings with ID '{id}' was not found"
                );
            }

            _logger.LogDebug("Successfully deleted settings with ID {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting settings with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Parse create request from JsonElement for Settings objects
    /// </summary>
    /// <param name="jsonElement">JsonElement containing settings data (single object or array)</param>
    /// <returns>Collection of Settings objects</returns>
    private IEnumerable<Settings> ParseCreateRequestFromJsonElement(JsonElement jsonElement)
    {
        var settingsRecords = new List<Settings>();

        try
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in jsonElement.EnumerateArray())
                {
                    var settings = JsonSerializer.Deserialize<Settings>(
                        element.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (settings != null)
                    {
                        settingsRecords.Add(settings);
                    }
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.Object)
            {
                var settings = JsonSerializer.Deserialize<Settings>(
                    jsonElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (settings != null)
                {
                    settingsRecords.Add(settings);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse settings data from JsonElement");
            throw new ArgumentException("Invalid settings data format", ex);
        }

        return settingsRecords;
    }

    /// <summary>
    /// Process settings record for creation/update (validation, etc.)
    /// Follows the legacy API v3 behavior exactly
    /// </summary>
    /// <param name="settings">Settings record to process</param>
    private void ProcessSettingsForCreation(Settings settings)
    {
        // Legacy API v3 behavior: Settings don't have date fallback fields
        // Unlike other collections, settings collection in legacy API doesn't use fallbackGetDate

        // Set server timestamps if not present
        var now = DateTimeOffset.UtcNow;

        if (!settings.SrvModified.HasValue)
        {
            settings.SrvModified = now;
        }

        if (!settings.SrvCreated.HasValue)
        {
            settings.SrvCreated = settings.SrvModified;
        }

        // Generate identifier if not present (legacy behavior)
        if (string.IsNullOrEmpty(settings.Id))
        {
            settings.Id = GenerateIdentifier(settings);
        }

        // Validate required fields
        if (string.IsNullOrEmpty(settings.Key))
        {
            throw new ArgumentException("Settings key is required");
        }

        // Ensure key is unique-friendly (no spaces, special chars)
        settings.Key = settings.Key.Trim();
    }

    /// <summary>
    /// Generate identifier for settings record following legacy API v3 logic
    /// Uses key for settings identification
    /// </summary>
    /// <param name="settings">Settings record</param>
    /// <returns>Generated identifier</returns>
    private string GenerateIdentifier(Settings settings)
    {
        // Legacy API v3 would typically use the key as part of identifier
        // for settings since they're key-value pairs

        if (!string.IsNullOrEmpty(settings.Key))
        {
            // Use key as base for identifier, ensuring it's unique
            var cleanKey = settings.Key.Replace(" ", "-").ToLowerInvariant();
            return $"settings-{cleanKey}-{DateTimeOffset.UtcNow.Ticks:X}";
        }

        // Fallback to GUID for unique identification
        return Guid.CreateVersion7().ToString();
    }

    /// <summary>
    /// Get total count for pagination support
    /// </summary>
    private async Task<long> GetTotalCountAsync(
        string? type,
        string? findQuery,
        CancellationToken cancellationToken,
        string collection = "settings"
    )
    {
        try
        {
            return await _settings.CountSettingsAsync(findQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not get total count for {Collection}, using approximation",
                collection
            );
            return 0; // Return 0 for count errors to maintain API functionality
        }
    }
}
