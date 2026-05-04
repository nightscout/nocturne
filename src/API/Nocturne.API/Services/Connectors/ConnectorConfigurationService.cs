using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nocturne.API.Hubs;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Domain service for connector configuration management. Stores and retrieves per-tenant connector
/// configuration in the database, merging database-stored JSON with environment-variable secrets at
/// read time so that sensitive credentials never persist in the database.
/// </summary>
/// <seealso cref="IConnectorConfigurationService"/>
public class ConnectorConfigurationService : IConnectorConfigurationService
{
    private readonly NocturneDbContext _context;
    private readonly ISecretEncryptionService _encryptionService;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly IAuditContext _auditContext;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ConnectorConfigurationService> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Dictionary<string, SyncDataType> SyncPropertyToDataType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SyncGlucose"] = SyncDataType.Glucose,
            ["SyncManualBG"] = SyncDataType.ManualBG,
            ["SyncBoluses"] = SyncDataType.Boluses,
            ["SyncCarbIntake"] = SyncDataType.CarbIntake,
            ["SyncBolusCalculations"] = SyncDataType.BolusCalculations,
            ["SyncNotes"] = SyncDataType.Notes,
            ["SyncDeviceEvents"] = SyncDataType.DeviceEvents,
            ["SyncStateSpans"] = SyncDataType.StateSpans,
            ["SyncProfiles"] = SyncDataType.Profiles,
            ["SyncDeviceStatus"] = SyncDataType.DeviceStatus,
            ["SyncActivity"] = SyncDataType.Activity,
            ["SyncFood"] = SyncDataType.Food,
        };

    public ConnectorConfigurationService(
        NocturneDbContext context,
        ISecretEncryptionService encryptionService,
        ISignalRBroadcastService broadcastService,
        IAuditContext auditContext,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<ConnectorConfigurationService> logger)
    {
        _context = context;
        _encryptionService = encryptionService;
        _broadcastService = broadcastService;
        _auditContext = auditContext;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConnectorConfigurationResponse?> GetConfigurationAsync(
        string connectorName,
        CancellationToken ct = default)
    {
        var entity = await _context.ConnectorConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

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
            ModifiedBy = entity.ModifiedBy,
            LastSyncAttempt = entity.LastSyncAttempt,
            LastSuccessfulSync = entity.LastSuccessfulSync,
            LastErrorMessage = entity.LastErrorMessage,
            LastErrorAt = entity.LastErrorAt,
            IsHealthy = entity.IsHealthy
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
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

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
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

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

        // When saving Nightscout connector secrets that include an API secret,
        // create a DirectGrant with the SHA-1 hash so existing uploaders keep working.
        await TryCreateLegacyGrantAsync(connectorName, secrets, ct);

        // Broadcast secrets update (note: doesn't reveal actual secrets)
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = connectorName,
            ChangeType = "secrets_updated",
            ModifiedBy = modifiedBy
        });
    }

    /// <summary>
    /// When a Nightscout connector's API secret is saved, creates a DirectGrant with the
    /// SHA-1 hash of the secret so that legacy uploaders authenticated via the old
    /// <c>api-secret</c> header continue to work without reconfiguration.
    /// </summary>
    private async Task TryCreateLegacyGrantAsync(
        string connectorName,
        Dictionary<string, string> secrets,
        CancellationToken ct)
    {
        // Only applies to the Nightscout connector
        if (!connectorName.Equals("nightscout", StringComparison.OrdinalIgnoreCase))
            return;

        // The secret key is "ApiSecret" (ConnectorPropertyKey enum name)
        if (!secrets.TryGetValue("ApiSecret", out var apiSecret)
            && !secrets.TryGetValue("apiSecret", out apiSecret))
            return;

        if (string.IsNullOrWhiteSpace(apiSecret))
            return;

        var subjectId = _auditContext.SubjectId;
        if (subjectId == null)
        {
            _logger.LogWarning("Cannot create legacy grant: no authenticated subject in audit context");
            return;
        }

        var sha1Hash = HashUtils.Sha1Hex(apiSecret);

        var alreadyExists = await _context.OAuthGrants
            .AnyAsync(g => g.TenantId == _context.TenantId
                        && g.LegacySecretHash == sha1Hash
                        && g.GrantType == OAuthGrantTypes.Direct
                        && g.RevokedAt == null, ct);

        if (alreadyExists)
        {
            _logger.LogDebug("Legacy grant for Nightscout API secret already exists, skipping creation");
            return;
        }

        var normalizedScopes = OAuthScopes.Normalize([OAuthScopes.HealthReadWrite]).ToList();

        var grant = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = null,
            SubjectId = subjectId.Value,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = normalizedScopes,
            Label = "Nightscout (migrated)",
            TokenHash = null,
            LegacySecretHash = sha1Hash,
            CreatedAt = DateTime.UtcNow,
        };

        _context.OAuthGrants.Add(grant);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created legacy DirectGrant {GrantId} for Nightscout migration (tenant {TenantId})",
            grant.Id, _context.TenantId);
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
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

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
        var dbConfigsList = await _context.ConnectorConfigurations
            .AsNoTracking()
            .ToListAsync(ct);
        var dbConfigs = dbConfigsList.ToDictionary(
            c => c.ConnectorName,
            StringComparer.OrdinalIgnoreCase);

        var result = new List<ConnectorStatusInfo>();

        foreach (var connector in allConnectors)
        {
            var hasDbConfig = dbConfigs.TryGetValue(connector.ConnectorName, out var dbConfig);

            // Read enabled from config JSON
            var isEnabled = hasDbConfig && GetEnabledFromConfig(dbConfig!.ConfigurationJson);

            var status = new ConnectorStatusInfo
            {
                ConnectorName = connector.ConnectorName.ToLowerInvariant(),
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
    /// Returns true if the field is not present (backwards compatibility - existing connectors default to enabled).
    /// Returns false only if explicitly set to false or there's a parsing error.
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
            // If enabled field is not present, default to true for backwards compatibility
            // This ensures existing connector configs continue to work after the migration
            return true;
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
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

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
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

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
        var configType = FindConfigurationType(connectorName);
        if (configType == null)
        {
            _logger.LogWarning("Unknown connector {ConnectorName} for effective config", connectorName);
            return null;
        }

        if (Activator.CreateInstance(configType) is not BaseConnectorConfiguration config)
        {
            _logger.LogWarning("Could not create configuration for connector {ConnectorName}", connectorName);
            return null;
        }

        var registration = configType.GetCustomAttribute<ConnectorRegistrationAttribute>();
        var bindingName = registration?.ConnectorName ?? connectorName;

        _configuration.BindConnectorConfiguration(
            config,
            bindingName
        );

        return GetEffectiveConfiguration(config);
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
        catch (Exception)
        {
            // Could not create default instance - continue without defaults
        }

        var allProps = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Get connector registration for environment variable prefix (used by ConnectorPropertyAttribute)
        var registration = configType.GetCustomAttribute<ConnectorRegistrationAttribute>();
        var envPrefix = registration?.EnvironmentPrefix;
        var supportedDataTypes = registration?.SupportedDataTypes ?? [SyncDataType.Glucose];

        foreach (var property in allProps)
        {
            var connectorPropAttr = property.GetCustomAttribute<ConnectorPropertyAttribute>();
            if (connectorPropAttr == null)
                continue;

            // Skip sync toggle properties for data types this connector doesn't support
            if (SyncPropertyToDataType.TryGetValue(property.Name, out var requiredDataType))
            {
                if (!supportedDataTypes.Contains(requiredDataType))
                    continue;
            }

            var propName = ToCamelCase(connectorPropAttr.GetKeyName());

            // Handle secret fields - include in schema and mark as secret
            if (connectorPropAttr.Secret)
            {
                secrets.Add(propName);

                var secretSchema = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["x-secret"] = true
                };

                if (!string.IsNullOrEmpty(envPrefix))
                {
                    secretSchema["x-envVar"] = connectorPropAttr.GetFullEnvVarName(envPrefix);
                }

                if (connectorPropAttr.Required)
                {
                    required.Add(propName);
                }

                properties[propName] = secretSchema;
                continue;
            }

            // Get default value from instance
            object? defaultValue = null;
            if (defaultInstance != null)
            {
                try
                {
                    defaultValue = property.GetValue(defaultInstance);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
            }

            var propertySchema = GeneratePropertySchema(
                property.PropertyType, connectorPropAttr, envPrefix, defaultValue);

            properties[propName] = propertySchema;

            if (connectorPropAttr.Required)
            {
                required.Add(propName);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
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

    private static Dictionary<string, object?> GetEffectiveConfiguration(IConnectorConfiguration config)
    {
        var result = new Dictionary<string, object?>();
        var configType = config.GetType();
        var registration = configType.GetCustomAttribute<ConnectorRegistrationAttribute>();
        var supportedDataTypes = registration?.SupportedDataTypes ?? [SyncDataType.Glucose];

        foreach (var property in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var connectorPropAttr = property.GetCustomAttribute<ConnectorPropertyAttribute>();
            if (connectorPropAttr == null)
                continue;

            if (connectorPropAttr.Secret)
                continue;

            // Skip sync toggle properties for data types this connector doesn't support
            if (SyncPropertyToDataType.TryGetValue(property.Name, out var requiredDataType))
            {
                if (!supportedDataTypes.Contains(requiredDataType))
                    continue;
            }

            object? value = null;
            try
            {
                value = property.GetValue(config);
            }
            catch
            {
                continue;
            }

            if (value != null && property.PropertyType.IsEnum)
            {
                value = value.ToString();
            }

            result[ToCamelCase(connectorPropAttr.GetKeyName())] = value;
        }

        return result;
    }

    /// <summary>
    /// Generates a JSON Schema property definition from a ConnectorPropertyAttribute.
    /// </summary>
    private static Dictionary<string, object> GeneratePropertySchema(
        Type propertyType,
        ConnectorPropertyAttribute connectorAttr,
        string? envPrefix,
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

        // Default value: prefer instance default, fall back to attribute DefaultValue
        if (defaultValue != null)
        {
            if (underlyingType.IsEnum)
            {
                schema["default"] = defaultValue.ToString()!;
            }
            else if (!IsDefaultOrEmpty(defaultValue))
            {
                schema["default"] = defaultValue;
            }
        }
        else if (!string.IsNullOrEmpty(connectorAttr.DefaultValue))
        {
            schema["default"] = connectorAttr.DefaultValue;
        }

        // Environment variable name
        if (!string.IsNullOrEmpty(envPrefix))
        {
            schema["x-envVar"] = connectorAttr.GetFullEnvVarName(envPrefix);
        }

        // Constraints
        if (connectorAttr.HasMinValue)
        {
            schema["minimum"] = connectorAttr.MinValue;
        }

        if (connectorAttr.HasMaxValue)
        {
            schema["maximum"] = connectorAttr.MaxValue;
        }

        if (connectorAttr.AllowedValues != null && connectorAttr.AllowedValues.Length > 0)
        {
            schema["enum"] = connectorAttr.AllowedValues;
        }

        if (!string.IsNullOrEmpty(connectorAttr.Format))
        {
            schema["format"] = connectorAttr.Format;
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

    /// <inheritdoc />
    public async Task<ConnectorHealthStateDto?> GetHealthStateAsync(
        string connectorName,
        CancellationToken ct = default
    )
    {
        var config = await _context.ConnectorConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

        if (config == null)
            return null;

        return new ConnectorHealthStateDto
        {
            LastSyncAttempt = config.LastSyncAttempt,
            LastSuccessfulSync = config.LastSuccessfulSync,
            LastErrorMessage = config.LastErrorMessage,
            LastErrorAt = config.LastErrorAt,
            IsHealthy = config.IsHealthy
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// To clear error fields, pass special values:
    /// - Pass string.Empty for lastErrorMessage to clear the error message
    /// - Pass DateTime.MinValue for lastErrorAt to clear the error timestamp
    /// </remarks>
    public async Task UpdateHealthStateAsync(
        string connectorName,
        DateTime? lastSyncAttempt = null,
        DateTime? lastSuccessfulSync = null,
        string? lastErrorMessage = null,
        DateTime? lastErrorAt = null,
        bool? isHealthy = null,
        CancellationToken ct = default
    )
    {
        var config = await _context.ConnectorConfigurations
            .FirstOrDefaultAsync(c => c.ConnectorName.ToLower() == connectorName.ToLower(), ct);

        if (config == null)
        {
            _logger.LogWarning(
                "Cannot update health state for connector {ConnectorName}: configuration not found",
                connectorName
            );
            return;
        }

        // Only update fields that were provided
        if (lastSyncAttempt.HasValue)
            config.LastSyncAttempt = lastSyncAttempt.Value;

        if (lastSuccessfulSync.HasValue)
            config.LastSuccessfulSync = lastSuccessfulSync.Value;

        if (lastErrorMessage != null)
        {
            if (lastErrorMessage == string.Empty)
                config.LastErrorMessage = null; // Explicit clear
            else
                config.LastErrorMessage = lastErrorMessage;
        }

        if (lastErrorAt.HasValue)
        {
            if (lastErrorAt.Value == DateTime.MinValue)
                config.LastErrorAt = null; // Explicit clear
            else
                config.LastErrorAt = lastErrorAt.Value;
        }

        if (isHealthy.HasValue)
            config.IsHealthy = isHealthy.Value;

        config.SysUpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Updated health state for connector {ConnectorName}: IsHealthy={IsHealthy}",
            connectorName,
            config.IsHealthy
        );
    }
}
