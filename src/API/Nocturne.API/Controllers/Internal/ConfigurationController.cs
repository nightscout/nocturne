using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts;

namespace Nocturne.API.Controllers.Internal;

/// <summary>
/// Internal API for connector configuration management.
/// This endpoint is intended for internal use by connectors via mTLS authentication.
/// In the initial implementation, it uses standard API authentication.
/// </summary>
[ApiController]
[Route("internal/config")]
[Authorize]
[ApiExplorerSettings(GroupName = "internal")]
public class ConfigurationController : ControllerBase
{
    private readonly IConnectorConfigurationService _configService;
    private readonly ILogger<ConfigurationController> _logger;

    public ConfigurationController(
        IConnectorConfigurationService configService,
        ILogger<ConfigurationController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the configuration for a specific connector.
    /// Returns runtime configuration only (secrets are not included).
    /// </summary>
    /// <param name="connectorName">The connector name (e.g., "Dexcom", "Glooko")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Configuration response or 404 if not found</returns>
    [HttpGet("{connectorName}")]
    [ProducesResponseType(typeof(ConnectorConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConnectorConfigurationResponse>> GetConfiguration(
        string connectorName,
        CancellationToken ct)
    {
        _logger.LogDebug("Getting configuration for connector {ConnectorName}", connectorName);

        var config = await _configService.GetConfigurationAsync(connectorName, ct);
        if (config == null)
        {
            return NotFound(new { message = $"No configuration found for connector '{connectorName}'" });
        }

        return Ok(config);
    }

    /// <summary>
    /// Gets the JSON Schema for a connector's configuration.
    /// Schema is generated from the connector's configuration class attributes.
    /// This endpoint is public since schema is just metadata, not sensitive data.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>JSON Schema document</returns>
    [HttpGet("{connectorName}/schema")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(JsonDocument), StatusCodes.Status200OK)]
    public async Task<ActionResult<JsonDocument>> GetSchema(
        string connectorName,
        CancellationToken ct)
    {
        _logger.LogDebug("Getting schema for connector {ConnectorName}", connectorName);

        var schema = await _configService.GetSchemaAsync(connectorName, ct);
        return Ok(schema);
    }

    /// <summary>
    /// Gets the effective configuration from a running connector.
    /// This returns the actual runtime values including those resolved from environment variables.
    /// This endpoint is public since it only exposes non-secret configuration values.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of property names to effective values</returns>
    [HttpGet("{connectorName}/effective")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(Dictionary<string, object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<Dictionary<string, object?>>> GetEffectiveConfiguration(
        string connectorName,
        CancellationToken ct)
    {
        _logger.LogDebug("Getting effective configuration for connector {ConnectorName}", connectorName);

        var effectiveConfig = await _configService.GetEffectiveConfigurationAsync(connectorName, ct);
        if (effectiveConfig == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = $"Connector '{connectorName}' is not available or not running"
            });
        }

        return Ok(effectiveConfig);
    }

    /// <summary>
    /// Saves or updates runtime configuration for a connector.
    /// Only properties marked with [RuntimeConfigurable] are accepted.
    /// Validates the configuration against the connector's schema before saving.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="configuration">Configuration values as JSON</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The saved configuration</returns>
    [HttpPut("{connectorName}")]
    [ProducesResponseType(typeof(ConnectorConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConnectorConfigurationResponse>> SaveConfiguration(
        string connectorName,
        [FromBody] JsonDocument configuration,
        CancellationToken ct)
    {
        var modifiedBy = User.Identity?.Name ?? "api";
        _logger.LogInformation("Saving configuration for connector {ConnectorName} by {ModifiedBy}",
            connectorName, modifiedBy);

        // Validate the configuration against the schema
        var validationErrors = await ValidateConfigurationAsync(connectorName, configuration, ct);
        if (validationErrors.Count > 0)
        {
            _logger.LogWarning("Configuration validation failed for connector {ConnectorName}: {Errors}",
                connectorName, string.Join(", ", validationErrors));
            return BadRequest(new { message = "Configuration validation failed", errors = validationErrors });
        }

        var result = await _configService.SaveConfigurationAsync(connectorName, configuration, modifiedBy, ct);
        return Ok(result);
    }

    /// <summary>
    /// Validates the configuration against the connector's schema.
    /// </summary>
    private async Task<List<string>> ValidateConfigurationAsync(
        string connectorName,
        JsonDocument configuration,
        CancellationToken ct)
    {
        var errors = new List<string>();

        // Get the schema for this connector
        var schema = await _configService.GetSchemaAsync(connectorName, ct);
        var schemaRoot = schema.RootElement;

        // Check if schema has properties defined
        if (!schemaRoot.TryGetProperty("properties", out var schemaProperties))
        {
            // No schema available, allow any configuration
            return errors;
        }

        var configRoot = configuration.RootElement;

        // Validate required fields
        if (schemaRoot.TryGetProperty("required", out var requiredFields))
        {
            foreach (var requiredField in requiredFields.EnumerateArray())
            {
                var fieldName = requiredField.GetString();
                if (fieldName != null && !configRoot.TryGetProperty(fieldName, out _))
                {
                    errors.Add($"Required field '{fieldName}' is missing");
                }
            }
        }

        // Validate each property in the configuration
        foreach (var configProp in configRoot.EnumerateObject())
        {
            // Skip 'enabled' field - it's always valid
            if (configProp.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if the property exists in the schema
            if (!schemaProperties.TryGetProperty(configProp.Name, out var propSchema))
            {
                // Unknown property - could be a secret field or removed property
                // Don't error, just skip validation for unknown fields
                continue;
            }

            // Validate type
            if (propSchema.TryGetProperty("type", out var typeProp))
            {
                var expectedType = typeProp.GetString();
                var actualValue = configProp.Value;

                var typeValid = expectedType switch
                {
                    "boolean" => actualValue.ValueKind == JsonValueKind.True || actualValue.ValueKind == JsonValueKind.False,
                    "integer" => actualValue.ValueKind == JsonValueKind.Number && actualValue.TryGetInt64(out _),
                    "number" => actualValue.ValueKind == JsonValueKind.Number,
                    "string" => actualValue.ValueKind == JsonValueKind.String,
                    _ => true
                };

                if (!typeValid)
                {
                    errors.Add($"Field '{configProp.Name}' has invalid type. Expected {expectedType}");
                }
            }

            // Validate minimum/maximum for numbers
            if (propSchema.TryGetProperty("minimum", out var minProp) && configProp.Value.ValueKind == JsonValueKind.Number)
            {
                var minValue = minProp.GetDouble();
                var actualValue = configProp.Value.GetDouble();
                if (actualValue < minValue)
                {
                    errors.Add($"Field '{configProp.Name}' value {actualValue} is less than minimum {minValue}");
                }
            }

            if (propSchema.TryGetProperty("maximum", out var maxProp) && configProp.Value.ValueKind == JsonValueKind.Number)
            {
                var maxValue = maxProp.GetDouble();
                var actualValue = configProp.Value.GetDouble();
                if (actualValue > maxValue)
                {
                    errors.Add($"Field '{configProp.Name}' value {actualValue} is greater than maximum {maxValue}");
                }
            }

            // Validate enum values
            if (propSchema.TryGetProperty("enum", out var enumProp) && configProp.Value.ValueKind == JsonValueKind.String)
            {
                var actualValue = configProp.Value.GetString();
                var validValues = enumProp.EnumerateArray().Select(v => v.GetString()).ToList();
                if (!validValues.Contains(actualValue))
                {
                    errors.Add($"Field '{configProp.Name}' value '{actualValue}' is not one of: {string.Join(", ", validValues)}");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Saves encrypted secrets for a connector.
    /// Secrets are encrypted using AES-256-GCM before storage.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="secrets">Dictionary of secret property names to plaintext values</param>
    /// <param name="ct">Cancellation token</param>
    [HttpPut("{connectorName}/secrets")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveSecrets(
        string connectorName,
        [FromBody] Dictionary<string, string> secrets,
        CancellationToken ct)
    {
        var modifiedBy = User.Identity?.Name ?? "api";
        _logger.LogInformation("Saving secrets for connector {ConnectorName} by {ModifiedBy}",
            connectorName, modifiedBy);

        try
        {
            await _configService.SaveSecretsAsync(connectorName, secrets, modifiedBy, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to save secrets - encryption not configured");
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Gets status information for all registered connectors.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of connector status information</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectorStatusInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ConnectorStatusInfo>>> GetAllConnectorStatus(
        CancellationToken ct)
    {
        _logger.LogDebug("Getting all connector status");

        var status = await _configService.GetAllConnectorStatusAsync(ct);
        return Ok(status);
    }

    /// <summary>
    /// Enables or disables a connector.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="request">Request containing the active state</param>
    /// <param name="ct">Cancellation token</param>
    [HttpPatch("{connectorName}/active")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetActive(
        string connectorName,
        [FromBody] SetActiveRequest request,
        CancellationToken ct)
    {
        var modifiedBy = User.Identity?.Name ?? "api";
        _logger.LogInformation("Setting connector {ConnectorName} active={IsActive} by {ModifiedBy}",
            connectorName, request.IsActive, modifiedBy);

        await _configService.SetActiveAsync(connectorName, request.IsActive, modifiedBy, ct);
        return NoContent();
    }

    /// <summary>
    /// Deletes all configuration and secrets for a connector.
    /// </summary>
    /// <param name="connectorName">The connector name</param>
    /// <param name="ct">Cancellation token</param>
    [HttpDelete("{connectorName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteConfiguration(
        string connectorName,
        CancellationToken ct)
    {
        _logger.LogInformation("Deleting configuration for connector {ConnectorName}", connectorName);

        var deleted = await _configService.DeleteConfigurationAsync(connectorName, ct);
        if (!deleted)
        {
            return NotFound(new { message = $"No configuration found for connector '{connectorName}'" });
        }

        return NoContent();
    }
}
