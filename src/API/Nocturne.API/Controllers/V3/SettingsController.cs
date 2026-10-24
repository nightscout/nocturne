using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Constants;
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
    [RequireScope(Scope.TherapyRead)]
    [ErrorEnvelope]
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

            // Get settings using existing backend with V3 parameters
            var settings = await _settings.GetSettingsWithAdvancedFilterAsync(
                count: parameters.Limit,
                skip: parameters.Offset,
                findQuery: findQuery,
                reverseResults: parameters.Sort.Descending,
                cancellationToken: cancellationToken
            );

            var settingsList = settings.ToList();

            var lastModified = GetLastModified(settingsList.Cast<object>());

            if (lastModified.HasValue && ShouldReturn304(lastModified.Value, parameters))
            {
                return StatusCode(304);
            }

            _logger.LogDebug(
                "Successfully returned {Count} settings with V3 format",
                settingsList.Count
            );

            // CreateV3CollectionResponse returns the {status, result} envelope IActionResult;
            // wrapping it in Ok(...) again would serialize the ActionResult object itself.
            return (ActionResult)CreateV3CollectionResponse(settingsList, parameters);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 settings request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
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
    [ProducesResponseType(typeof(V3Envelope<Settings>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.TherapyRead)]
    [ErrorEnvelope]
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

        var parameters = ParseV3QueryParameters(readSort: false); // Apply field selection if specified
        var result = ApplyFieldSelection(new[] { settings }, parameters.Fields).First();

        _logger.LogDebug("Successfully returned settings with ID {Id}", id);

        // Upstream's v3 READ answers {status, result}; AAPS reads the document from result.
        return CreateV3SuccessResponse(result);
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
    [RequireScope(Scope.TherapyReadWrite)]
    [NightscoutEndpoint("/api/v3/settings")]
    [ProducesResponseType(typeof(Settings[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
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
    }

    /// <summary>
    /// Replace the settings document with this identifier, or insert it when none is stored:
    /// upstream's v3 UPDATE, which AAPS uses to keep its <c>aaps</c> document and its
    /// client-control pairing offers.
    /// </summary>
    /// <param name="id">Identifier of the document, which is its key</param>
    /// <param name="settingsData">The whole document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>201 with the identifier on insert, 200 on replace</returns>
    [HttpPut("{id}")]
    [Authorize]
    [RequireScope(Scope.TherapyReadWrite)]
    [NightscoutEndpoint("/api/v3/settings/{id}")]
    [ProducesResponseType(typeof(V3CreateResponse), 201)]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> UpdateSettings(
        string id,
        [FromBody] JsonElement settingsData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 settings update endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        var validationError = ValidateV3CommonFields(settingsData);
        if (validationError != null)
        {
            return validationError;
        }

        Settings? settings;
        try
        {
            settings = NightscoutJsonBody.ReadObject<Settings>(WithoutServerFields(settingsData));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in V3 settings update request for ID {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }

        if (settings == null)
        {
            return CreateV3ErrorResponse(
                400,
                "Invalid request body",
                "Request body must contain valid settings data"
            );
        }

        if (SettingsKeys.IsReserved(id))
        {
            return CreateV3ErrorResponse(400, "Invalid request data", $"Settings key '{id}' is reserved.");
        }

        settings.SrvModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!await _settings.PutSettingsAsync(id, settings, cancellationToken))
        {
            _logger.LogDebug("Replaced settings with ID {Id}", id);
            return V3Replaced();
        }

        _logger.LogDebug("Inserted settings with ID {Id}", id);
        return V3Inserted(nameof(GetSettingsById), id);
    }

    /// <summary>
    /// The document without the fields the server owns. A writer echoes back what it last read,
    /// and upstream sets these itself on every write rather than taking them from the body.
    /// </summary>
    private static JsonElement WithoutServerFields(JsonElement document)
    {
        var fields = new JsonObject();
        foreach (var field in document.EnumerateObject())
        {
            // Case-insensitive, as the body binds: "_ID" would otherwise reach Settings.Id.
            if (!ServerFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
            {
                fields[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            }
        }

        return JsonSerializer.SerializeToElement(fields);
    }

    private static readonly string[] ServerFields =
        ["_id", "identifier", "key", "srvCreated", "srvModified", "subject"];

    /// <summary>
    /// Delete a settings record by ID
    /// Requires admin permissions as per legacy API v3 behavior
    /// </summary>
    /// <param name="id">Settings ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns><c>{"status": 200}</c> on success</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(Scope.TherapyReadWrite)]
    [NightscoutEndpoint("/api/v3/settings/{id}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 403)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
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

        return CreateV3DeleteResponse();
    }

    /// <summary>
    /// Parse create request from JsonElement for Settings objects
    /// </summary>
    /// <param name="jsonElement">JsonElement containing settings data (single object or array)</param>
    /// <returns>Collection of Settings objects</returns>
    private IEnumerable<Settings> ParseCreateRequestFromJsonElement(JsonElement jsonElement)
    {
        try
        {
            return NightscoutJsonBody.ReadObjects<Settings>(jsonElement) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse settings data from JsonElement");
            throw new ArgumentException("Invalid settings data format", ex);
        }
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
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
}
