using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Hubs;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services;

/// <summary>
/// Service for managing connector configurations stored in the database.
/// Handles merging of environment variables (secrets) with database-stored runtime configuration.
/// </summary>
public class ConnectorConfigurationService : IConnectorConfigurationService
{
    private readonly NocturneDbContext _context;
    private readonly ISecretEncryptionService _encryptionService;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConnectorConfigurationService> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Maps connector IDs to their Aspire service names for HTTP calls.
    /// </summary>
    private static readonly Dictionary<string, string> ConnectorServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nightscout"] = ServiceNames.NightscoutConnector,
        ["dexcom"] = ServiceNames.DexcomConnector,
        ["libre"] = ServiceNames.LibreConnector,
        ["glooko"] = ServiceNames.GlookoConnector,
        ["carelink"] = ServiceNames.MiniMedConnector,
        ["myfitnesspal"] = ServiceNames.MyFitnessPalConnector,
        ["mylife"] = ServiceNames.MyLifeConnector,
    };

    public ConnectorConfigurationService(
        NocturneDbContext context,
        ISecretEncryptionService encryptionService,
        ISignalRBroadcastService broadcastService,
        IHttpClientFactory httpClientFactory,
        ILogger<ConnectorConfigurationService> logger)
    {
        _context = context;
        _encryptionService = encryptionService;
        _broadcastService = broadcastService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConnectorConfigurationResponse?> GetConfigurationAsync(
        string connectorName,
        CancellationToken ct = default)
    {
        var entity = await _context.ConnectorConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);

        if (entity == null)
        {
            _logger.LogDebug("No configuration found for connector {ConnectorName}", connectorName);
            return null;
        }

        // Read enabled from config JSON
        var isActive = GetEnabledFromConfig(entity.ConfigurationJson);

        var response = new ConnectorConfigurationResponse
        {
            ConnectorName = entity.ConnectorName,
            Configuration = JsonDocument.Parse(entity.ConfigurationJson),
            SchemaVersion = entity.SchemaVersion,
            IsActive = isActive,
            LastModified = entity.LastModified,
            ModifiedBy = entity.ModifiedBy
        };

        return response;
    }

    /// <inheritdoc />
    public async Task<ConnectorConfigurationResponse> SaveConfigurationAsync(
        string connectorName,
        JsonDocument configuration,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        var entity = await _context.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);

        var configJson = configuration.RootElement.GetRawText();

        // Read the enabled state from the config JSON (defaults to false if not present)
        var enabledFromConfig = GetEnabledFromConfig(configJson);

        if (entity == null)
        {
            entity = new ConnectorConfigurationEntity
            {
                ConnectorName = connectorName,
                ConfigurationJson = configJson,
                SecretsJson = "{}",
                LastModified = DateTimeOffset.UtcNow,
                ModifiedBy = modifiedBy
            };
            _context.ConnectorConfigurations.Add(entity);
            _logger.LogInformation("Creating new configuration for connector {ConnectorName}", connectorName);
        }
        else
        {
            entity.ConfigurationJson = configJson;
            entity.LastModified = DateTimeOffset.UtcNow;
            entity.ModifiedBy = modifiedBy;
            _logger.LogInformation("Updating configuration for connector {ConnectorName}", connectorName);
        }

        await _context.SaveChangesAsync(ct);

        // Broadcast configuration change
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = connectorName,
            ChangeType = "updated",
            ModifiedBy = modifiedBy
        });

        return new ConnectorConfigurationResponse
        {
            ConnectorName = entity.ConnectorName,
            Configuration = JsonDocument.Parse(entity.ConfigurationJson),
            SchemaVersion = entity.SchemaVersion,
            IsActive = enabledFromConfig,
            LastModified = entity.LastModified,
            ModifiedBy = entity.ModifiedBy
        };
    }

    /// <inheritdoc />
    public async Task SaveSecretsAsync(
        string connectorName,
        Dictionary<string, string> secrets,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        if (!_encryptionService.IsConfigured)
        {
            throw new InvalidOperationException(
                "Secret encryption is not configured. Ensure api-secret is set in configuration.");
        }

        var entity = await _context.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);

        var encryptedSecrets = _encryptionService.EncryptSecrets(secrets);
        var secretsJson = JsonSerializer.Serialize(encryptedSecrets, _jsonOptions);

        if (entity == null)
        {
            entity = new ConnectorConfigurationEntity
            {
                ConnectorName = connectorName,
                ConfigurationJson = "{}",
                SecretsJson = secretsJson,
                LastModified = DateTimeOffset.UtcNow,
                ModifiedBy = modifiedBy
            };
            _context.ConnectorConfigurations.Add(entity);
            _logger.LogInformation("Creating new secrets for connector {ConnectorName}", connectorName);
        }
        else
        {
            entity.SecretsJson = secretsJson;
            entity.LastModified = DateTimeOffset.UtcNow;
            entity.ModifiedBy = modifiedBy;
            _logger.LogInformation("Updating secrets for connector {ConnectorName}", connectorName);
        }

        await _context.SaveChangesAsync(ct);

        // Broadcast secrets update (note: doesn't reveal actual secrets)
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = connectorName,
            ChangeType = "secrets_updated",
            ModifiedBy = modifiedBy
        });
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetSecretsAsync(
        string connectorName,
        CancellationToken ct = default)
    {
        if (!_encryptionService.IsConfigured)
        {
            _logger.LogWarning("Secret encryption not configured, returning empty secrets for {ConnectorName}", connectorName);
            return new Dictionary<string, string>();
        }

        var entity = await _context.ConnectorConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);

        if (entity == null || string.IsNullOrEmpty(entity.SecretsJson) || entity.SecretsJson == "{}")
        {
            return new Dictionary<string, string>();
        }

        var encryptedSecrets = JsonSerializer.Deserialize<Dictionary<string, string>>(
            entity.SecretsJson, _jsonOptions) ?? new Dictionary<string, string>();

        return _encryptionService.DecryptSecrets(encryptedSecrets);
    }

    /// <inheritdoc />
    public Task<JsonDocument> GetSchemaAsync(string connectorName, CancellationToken ct = default)
    {
        var connectorInfo = ConnectorMetadataService.GetByConnectorId(connectorName);
        if (connectorInfo == null)
        {
            _logger.LogWarning("Unknown connector {ConnectorName}, returning empty schema", connectorName);
            return Task.FromResult(JsonDocument.Parse("{}"));
        }

        // Find the configuration class type
        var configType = FindConfigurationType(connectorName);
        if (configType == null)
        {
            _logger.LogWarning("Could not find configuration type for connector {ConnectorName}", connectorName);
            return Task.FromResult(JsonDocument.Parse("{}"));
        }

        var schema = GenerateSchemaFromType(configType);
        return Task.FromResult(schema);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorStatusInfo>> GetAllConnectorStatusAsync(CancellationToken ct = default)
    {
        var allConnectors = ConnectorMetadataService.GetAll();
        var dbConfigs = await _context.ConnectorConfigurations
            .AsNoTracking()
            .ToDictionaryAsync(c => c.ConnectorName, ct);

        var result = new List<ConnectorStatusInfo>();

        foreach (var connector in allConnectors)
        {
            var hasDbConfig = dbConfigs.TryGetValue(connector.ConnectorName, out var dbConfig);

            // Read enabled from config JSON
            var isEnabled = hasDbConfig && GetEnabledFromConfig(dbConfig!.ConfigurationJson);

            var status = new ConnectorStatusInfo
            {
                ConnectorName = connector.ConnectorName,
                IsEnabled = isEnabled,
                HasDatabaseConfig = hasDbConfig,
                HasSecrets = hasDbConfig && !string.IsNullOrEmpty(dbConfig!.SecretsJson) && dbConfig.SecretsJson != "{}",
                LastModified = hasDbConfig ? dbConfig!.LastModified : null
            };

            result.Add(status);
        }

        return result;
    }

    /// <summary>
    /// Reads the enabled field from the configuration JSON.
    /// Returns false if the field is not present or there's a parsing error.
    /// </summary>
    private bool GetEnabledFromConfig(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("enabled", out var enabledProp))
            {
                return enabledProp.GetBoolean();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse configuration JSON for enabled field");
        }
        return false;
    }

    /// <inheritdoc />
    public async Task SetActiveAsync(
        string connectorName,
        bool isActive,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        var entity = await _context.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);

        // Create the config JSON with the enabled field
        var configWithEnabled = CreateConfigWithEnabled(entity?.ConfigurationJson ?? "{}", isActive);

        if (entity == null)
        {
            entity = new ConnectorConfigurationEntity
            {
                ConnectorName = connectorName,
                ConfigurationJson = configWithEnabled,
                SecretsJson = "{}",
                LastModified = DateTimeOffset.UtcNow,
                ModifiedBy = modifiedBy
            };
            _context.ConnectorConfigurations.Add(entity);
        }
        else
        {
            entity.ConfigurationJson = configWithEnabled;
            entity.LastModified = DateTimeOffset.UtcNow;
            entity.ModifiedBy = modifiedBy;
        }

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Set connector {ConnectorName} active={IsActive}", connectorName, isActive);

        // Broadcast enable/disable change
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = connectorName,
            ChangeType = isActive ? "enabled" : "disabled",
            ModifiedBy = modifiedBy
        });
    }

    /// <summary>
    /// Updates the configuration JSON to include the enabled field.
    /// </summary>
    private static string CreateConfigWithEnabled(string existingConfigJson, bool enabled)
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingConfigJson, _jsonOptions)
            ?? new Dictionary<string, JsonElement>();

        // Remove existing enabled key if present (case-insensitive)
        var keyToRemove = config.Keys.FirstOrDefault(k => k.Equals("enabled", StringComparison.OrdinalIgnoreCase));
        if (keyToRemove != null)
        {
            config.Remove(keyToRemove);
        }

        // Add the enabled value using a temporary JSON document
        using var doc = JsonDocument.Parse(enabled ? "true" : "false");
        config["enabled"] = doc.RootElement.Clone();

        return JsonSerializer.Serialize(config, _jsonOptions);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteConfigurationAsync(string connectorName, CancellationToken ct = default)
    {
        var entity = await _context.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);

        if (entity == null)
        {
            return false;
        }

        _context.ConnectorConfigurations.Remove(entity);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted configuration for connector {ConnectorName}", connectorName);

        // Broadcast deletion
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = connectorName,
            ChangeType = "deleted"
        });

        return true;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object?>?> GetEffectiveConfigurationAsync(
        string connectorName,
        CancellationToken ct = default)
    {
        if (!ConnectorServiceNames.TryGetValue(connectorName, out var serviceName))
        {
            _logger.LogWarning("Unknown connector {ConnectorName} for effective config", connectorName);
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(ConnectorHealthService.HttpClientName);
            var url = $"http://{serviceName}/config/effective";

            _logger.LogDebug("Fetching effective config for {Connector} at {Url}", connectorName, url);

            var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to get effective config for {Connector}: {StatusCode}",
                    connectorName,
                    response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, _jsonOptions);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch effective config for {Connector}", connectorName);
            return null;
        }
    }

    /// <summary>
    /// Finds the configuration class Type for a given connector name.
    /// </summary>
    private static Type? FindConfigurationType(string connectorName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Nocturne.Connectors") == true)
            .ToList();

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    var attr = type.GetCustomAttribute<ConnectorRegistrationAttribute>();
                    if (attr != null && attr.ConnectorName.Equals(connectorName, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Some types may not be loadable, skip them
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a JSON Schema from a configuration type based on attributes.
    /// Only includes properties marked with [RuntimeConfigurable].
    /// Includes default values and environment variable names for UI display.
    /// </summary>
    private static JsonDocument GenerateSchemaFromType(Type configType)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        var secrets = new List<string>();

        // Create an instance to get default values
        object? defaultInstance = null;
        try
        {
            defaultInstance = Activator.CreateInstance(configType);
        }
        catch
        {
            // Could not create default instance - continue without defaults
        }

        var allProps = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in allProps)
        {
            var secretAttr = property.GetCustomAttribute<SecretAttribute>();
            var runtimeAttr = property.GetCustomAttribute<RuntimeConfigurableAttribute>();

            // Handle secret fields - include in schema and mark as secret
            if (secretAttr != null)
            {
                var envVarAttr = property.GetCustomAttribute<EnvironmentVariableAttribute>();
                var propName = ToCamelCase(property.Name);
                secrets.Add(propName);

                // Add secret property schema with title and description
                var secretSchema = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["title"] = FormatPropertyNameForDisplay(property.Name)
                };

                if (envVarAttr != null && !string.IsNullOrEmpty(envVarAttr.Name))
                {
                    secretSchema["x-envVar"] = envVarAttr.Name;
                    secretSchema["description"] = $"Configure via environment variable: {envVarAttr.Name}";
                }
                else
                {
                    secretSchema["description"] = "Sensitive credential (stored encrypted)";
                }

                properties[propName] = secretSchema;
                continue;
            }

            if (runtimeAttr == null)
            {
                continue; // Only include runtime-configurable properties
            }

            var schemaAttr = property.GetCustomAttribute<ConfigSchemaAttribute>();
            var envVarAttr2 = property.GetCustomAttribute<EnvironmentVariableAttribute>();

            // Get default value from instance
            object? defaultValue = null;
            if (defaultInstance != null)
            {
                try
                {
                    defaultValue = property.GetValue(defaultInstance);
                }
                catch
                {
                    // Ignore errors getting default value
                }
            }

            var propertySchema = GeneratePropertySchema(property.PropertyType, runtimeAttr, schemaAttr, envVarAttr2, defaultValue);

            properties[ToCamelCase(property.Name)] = propertySchema;

            // Check for Required attribute
            if (property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() != null)
            {
                required.Add(ToCamelCase(property.Name));
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["title"] = configType.Name,
            ["properties"] = properties
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        if (secrets.Count > 0)
        {
            schema["secrets"] = secrets;
        }

        var json = JsonSerializer.Serialize(schema, _jsonOptions);
        return JsonDocument.Parse(json);
    }

    private static string FormatPropertyNameForDisplay(string name)
    {
        // Convert camelCase/PascalCase to Title Case with spaces
        var result = System.Text.RegularExpressions.Regex.Replace(name, "([A-Z])", " $1").Trim();
        return char.ToUpperInvariant(result[0]) + result.Substring(1);
    }

    /// <summary>
    /// Generates a JSON Schema property definition for a property.
    /// Includes default value and environment variable name for UI display.
    /// </summary>
    private static Dictionary<string, object> GeneratePropertySchema(
        Type propertyType,
        RuntimeConfigurableAttribute runtimeAttr,
        ConfigSchemaAttribute? schemaAttr,
        EnvironmentVariableAttribute? envVarAttr,
        object? defaultValue)
    {
        var schema = new Dictionary<string, object>();

        // Determine JSON Schema type
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (underlyingType == typeof(bool))
        {
            schema["type"] = "boolean";
        }
        else if (underlyingType == typeof(int) || underlyingType == typeof(long) ||
                 underlyingType == typeof(short) || underlyingType == typeof(byte))
        {
            schema["type"] = "integer";
        }
        else if (underlyingType == typeof(float) || underlyingType == typeof(double) ||
                 underlyingType == typeof(decimal))
        {
            schema["type"] = "number";
        }
        else if (underlyingType.IsEnum)
        {
            schema["type"] = "string";
            schema["enum"] = Enum.GetNames(underlyingType);
        }
        else
        {
            schema["type"] = "string";
        }

        // Add title/description from RuntimeConfigurable
        if (!string.IsNullOrEmpty(runtimeAttr.DisplayName))
        {
            schema["title"] = runtimeAttr.DisplayName;
        }

        if (!string.IsNullOrEmpty(runtimeAttr.Description))
        {
            schema["description"] = runtimeAttr.Description;
        }

        // Add category for UI grouping
        if (!string.IsNullOrEmpty(runtimeAttr.Category))
        {
            schema["x-category"] = runtimeAttr.Category;
        }

        // Add default value if available
        if (defaultValue != null)
        {
            // Handle enums specially - convert to string
            if (underlyingType.IsEnum)
            {
                schema["default"] = defaultValue.ToString();
            }
            else if (!IsDefaultOrEmpty(defaultValue))
            {
                schema["default"] = defaultValue;
            }
        }

        // Add environment variable name for UI display
        if (envVarAttr != null && !string.IsNullOrEmpty(envVarAttr.Name))
        {
            schema["x-envVar"] = envVarAttr.Name;
        }

        // Add constraints from ConfigSchema
        if (schemaAttr != null)
        {
            if (schemaAttr.HasMinimum)
            {
                schema["minimum"] = schemaAttr.Minimum;
            }

            if (schemaAttr.HasMaximum)
            {
                schema["maximum"] = schemaAttr.Maximum;
            }

            if (schemaAttr.HasMinLength)
            {
                schema["minLength"] = schemaAttr.MinLength;
            }

            if (schemaAttr.HasMaxLength)
            {
                schema["maxLength"] = schemaAttr.MaxLength;
            }

            if (!string.IsNullOrEmpty(schemaAttr.Pattern))
            {
                schema["pattern"] = schemaAttr.Pattern;
            }

            if (schemaAttr.Enum != null && schemaAttr.Enum.Length > 0)
            {
                schema["enum"] = schemaAttr.Enum;
            }

            if (!string.IsNullOrEmpty(schemaAttr.Format))
            {
                schema["format"] = schemaAttr.Format;
            }
        }

        return schema;
    }

    /// <summary>
    /// Checks if a value is the default/empty value for its type.
    /// </summary>
    private static bool IsDefaultOrEmpty(object value)
    {
        if (value == null) return true;

        var type = value.GetType();

        // Empty strings
        if (value is string s && string.IsNullOrEmpty(s)) return true;

        // Default numerics (0)
        if (type == typeof(int) && (int)value == 0) return false; // 0 is a valid default
        if (type == typeof(double) && (double)value == 0.0) return false;
        if (type == typeof(float) && (float)value == 0.0f) return false;
        if (type == typeof(long) && (long)value == 0) return false;
        if (type == typeof(decimal) && (decimal)value == 0) return false;

        return false;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
