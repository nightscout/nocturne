using System.Reflection;
using System.Text.Json;
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
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Connectors;
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
    private readonly IConnectorConfigurationRepository _configurations;
    private readonly ISubjectRepository _subjects;
    private readonly IOAuthGrantRepository _grants;
    private readonly ISecretEncryptionService _encryptionService;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly IAuditContext _auditContext;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IEnumerable<IConnectorCacheInvalidator> _cacheInvalidators;
    private readonly ILogger<ConnectorConfigurationService> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <param name="grants">The stored grants, which a saved Nightscout secret is adopted into.</param>
    public ConnectorConfigurationService(
        IConnectorConfigurationRepository configurations,
        ISubjectRepository subjects,
        IOAuthGrantRepository grants,
        ISecretEncryptionService encryptionService,
        ISignalRBroadcastService broadcastService,
        IAuditContext auditContext,
        IConfiguration configuration,
        IHostEnvironment environment,
        IEnumerable<IConnectorCacheInvalidator> cacheInvalidators,
        ILogger<ConnectorConfigurationService> logger)
    {
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _encryptionService = encryptionService;
        _broadcastService = broadcastService;
        _auditContext = auditContext;
        _configuration = configuration;
        _environment = environment;
        _cacheInvalidators = cacheInvalidators;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConnectorConfigurationResponse?> GetConfigurationAsync(
        string connectorName,
        CancellationToken ct = default)
    {
        var entity = await _configurations.FindAsync(connectorName, ct);

        if (entity == null)
        {
            _logger.LogDebug("No configuration found for connector {ConnectorName}", connectorName);
            return null;
        }

        var response = new ConnectorConfigurationResponse
        {
            ConnectorName = entity.ConnectorName,
            Configuration = JsonDocument.Parse(entity.ConfigurationJson),
            SchemaVersion = entity.SchemaVersion,
            IsActive = ConnectorEnabledKey.IsEnabled(entity.ConfigurationJson),
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

    /// <summary>
    /// Refuses a write from a demo tenant's shared visitor account.
    /// </summary>
    /// <remarks>
    /// Guarded on the asset rather than only on the controllers that reach it. Connector
    /// configuration names a host the server will fetch from, and <c>GET</c> returns it in the
    /// clear to any member — and every demo visitor is the same member. Gating controllers missed
    /// <c>CareLinkConnectController</c>, which writes the visitor's real CareLink username and
    /// country here after a Medtronic sign-in: a real person's health-account identifier, handed
    /// to every later visitor, with their CGM data pulled into the shared tenant. Anything that
    /// writes connector configuration in future is covered without needing to remember an
    /// attribute. Applied to all four write methods on
    /// <see cref="Nocturne.Core.Contracts.Connectors.IConnectorConfigurationService"/> —
    /// <c>SaveConfigurationAsync</c>, <c>SaveSecretsAsync</c>, <c>SetActiveAsync</c> and
    /// <c>DeleteConfigurationAsync</c>. Enabling a connector or deleting its configuration are as
    /// much a shared-account concern as writing a URL is.
    /// <para>
    /// The subject comes from <see cref="IAuditContext"/>, which
    /// <c>AuditContextMiddleware</c> populates from the request's auth context. A caller with no
    /// subject (instance key, background work) is not a demo visitor and is left alone.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The caller is the demo visitor account.</exception>
    private async Task EnsureNotDemoSubjectAsync(string connectorName, CancellationToken ct)
    {
        if (_auditContext.SubjectId is not { } subjectId)
            return;

        if (await _subjects.IsDemoSubjectAsync(subjectId, ct) is not true)
            return;

        _logger.LogWarning(
            "Refusing to write {ConnectorName} configuration: the caller is the shared demo account",
            connectorName);

        throw new UnauthorizedAccessException(
            "The demo account cannot change connector configuration.");
    }

    /// <inheritdoc />
    public async Task<ConnectorConfigurationResponse> SaveConfigurationAsync(
        string connectorName,
        JsonDocument configuration,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        await EnsureNotDemoSubjectAsync(connectorName, ct);

        var canonicalName = ConnectorNames.Canonical(connectorName);
        var configJson = configuration.RootElement.GetRawText();

        var entity = await _configurations.SaveConfigurationAsync(canonicalName, configJson, modifiedBy, ct);
        _logger.LogInformation("Saved configuration for connector {ConnectorName}", connectorName);
        InvalidateCaches(entity);

        // Broadcast configuration change
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = canonicalName,
            ChangeType = "updated",
            ModifiedBy = modifiedBy
        });

        return new ConnectorConfigurationResponse
        {
            ConnectorName = entity.ConnectorName,
            Configuration = JsonDocument.Parse(entity.ConfigurationJson),
            SchemaVersion = entity.SchemaVersion,
            IsActive = ConnectorEnabledKey.IsEnabled(entity.ConfigurationJson),
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
        await EnsureNotDemoSubjectAsync(connectorName, ct);

        if (!_encryptionService.IsConfigured)
        {
            throw new InvalidOperationException(
                "Secret encryption is not configured. Ensure api-secret is set in configuration.");
        }

        var canonicalName = ConnectorNames.Canonical(connectorName);
        var encryptedSecrets = _encryptionService.EncryptSecrets(secrets);
        var secretsJson = JsonSerializer.Serialize(encryptedSecrets, _jsonOptions);

        var entity = await _configurations.SaveSecretsAsync(canonicalName, secretsJson, modifiedBy, ct);
        _logger.LogInformation("Saved secrets for connector {ConnectorName}", connectorName);
        InvalidateCaches(entity);

        // When saving Nightscout connector secrets that include an API secret,
        // create a DirectGrant with the SHA-1 hash so existing uploaders keep working.
        await TryCreateLegacyGrantAsync(connectorName, secrets, ct);

        // Broadcast secrets update (note: doesn't reveal actual secrets)
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = canonicalName,
            ChangeType = "secrets_updated",
            ModifiedBy = modifiedBy
        });
    }

    /// <summary>
    /// The Nightscout API secret a save would adopt as a working api-secret credential, or null
    /// when it adopts none. The key is the <c>ApiSecret</c> connector property in either casing.
    /// </summary>
    public static string? AdoptedSecret(string connectorName, IReadOnlyDictionary<string, string> secrets)
    {
        if (!connectorName.Equals("nightscout", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!secrets.TryGetValue("ApiSecret", out var apiSecret)
            && !secrets.TryGetValue("apiSecret", out apiSecret))
            return null;

        return string.IsNullOrWhiteSpace(apiSecret) ? null : apiSecret;
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
        if (AdoptedSecret(connectorName, secrets) is not { } apiSecret)
            return;

        // Adopting a credential is a person's decision, made by saving it from the connector page;
        // a job that writes the connector's secrets on its own does not hand out a working key.
        if (_auditContext.SubjectId is null)
        {
            _logger.LogWarning("Not adopting the Nightscout API secret: no authenticated subject saved it");
            return;
        }

        // SHA-1 is required here: the Nightscout `api-secret` protocol hashes the secret
        // with SHA-1, so legacy clients (xDrip, etc.) send that hash and we must match it.
        var sha1Hash = HashUtils.Sha1Hex(apiSecret);

        if (await _grants.HasUnrevokedLegacySecretAsync(sha1Hash, ct))
        {
            _logger.LogDebug("Legacy grant for Nightscout API secret already exists, skipping creation");
            return;
        }

        // A site's secret, not the saver's: see ISubjectRepository.FindDeviceSubjectAsync.
        var grantId = await _grants.AdoptLegacyCredentialAsync(
            new AdoptedLegacyCredential(
                await _subjects.EnsureDeviceSubjectAsync(ct),
                "Nightscout (migrated)",
                [Scope.HealthReadWrite],
                LegacySecretHash: sha1Hash),
            ct);

        _logger.LogInformation("Created legacy DirectGrant {GrantId} for Nightscout migration", grantId);
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

        var entity = await _configurations.FindAsync(connectorName, ct);

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
        var configType = FindConfigurationType(connectorName, _logger);
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
        var dbConfigsList = await _configurations.ListAsync(ct);
        var dbConfigs = dbConfigsList.ToDictionary(
            c => c.ConnectorName,
            StringComparer.OrdinalIgnoreCase);

        var result = new List<ConnectorStatusInfo>();

        foreach (var connector in allConnectors)
        {
            var hasDbConfig = dbConfigs.TryGetValue(connector.ConnectorName, out var dbConfig);

            var isEnabled = hasDbConfig && ConnectorEnabledKey.IsEnabled(dbConfig!.ConfigurationJson);

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

    /// <inheritdoc />
    public async Task SetActiveAsync(
        string connectorName,
        bool isActive,
        string? modifiedBy = null,
        CancellationToken ct = default)
    {
        await EnsureNotDemoSubjectAsync(connectorName, ct);

        var canonicalName = ConnectorNames.Canonical(connectorName);
        var entity = await _configurations.SetActiveAsync(canonicalName, isActive, modifiedBy, ct);
        InvalidateCaches(entity);
        _logger.LogInformation("Set connector {ConnectorName} active={IsActive}", connectorName, isActive);

        // Broadcast enable/disable change
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = canonicalName,
            ChangeType = isActive ? "enabled" : "disabled",
            ModifiedBy = modifiedBy
        });
    }

    /// <summary>
    /// Tells every tenant-keyed cache — auth tokens, sessions, the pollers' schedules — that this
    /// connector's stored configuration changed, so the next sync reads it afresh, a minute after
    /// the write.
    /// </summary>
    private void InvalidateCaches(ConnectorConfigurationRecord written)
    {
        foreach (var invalidator in _cacheInvalidators)
            invalidator.Invalidate(written.ConnectorName, written.TenantId);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteConfigurationAsync(string connectorName, CancellationToken ct = default)
    {
        await EnsureNotDemoSubjectAsync(connectorName, ct);

        var canonicalName = ConnectorNames.Canonical(connectorName);
        var entity = await _configurations.DeleteAsync(canonicalName, ct);

        if (entity == null)
        {
            return false;
        }

        InvalidateCaches(entity);
        _logger.LogInformation("Deleted configuration for connector {ConnectorName}", connectorName);

        // Broadcast deletion
        await _broadcastService.BroadcastConfigChangeAsync(new ConfigurationChangeEvent
        {
            ConnectorName = canonicalName,
            ChangeType = "deleted"
        });

        return true;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object?>?> GetEffectiveConfigurationAsync(
        string connectorName,
        CancellationToken ct = default)
    {
        var configType = FindConfigurationType(connectorName, _logger);
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
    private static Type? FindConfigurationType(string connectorName, ILogger logger)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Nocturne.Connectors") == true)
            .ToList();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.LoadableTypes(logger))
            {
                var attr = type.GetCustomAttribute<ConnectorRegistrationAttribute>();
                if (attr != null && attr.ConnectorName.Equals(connectorName, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
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
            if (ConnectorSyncToggles.ByPropertyKey.TryGetValue(connectorPropAttr.Key, out var gatedDataType)
                && !supportedDataTypes.Contains(gatedDataType))
                continue;

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

                if (connectorPropAttr.Hidden)
                {
                    secretSchema["x-hidden"] = true;
                }

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
            if (ConnectorSyncToggles.ByPropertyKey.TryGetValue(connectorPropAttr.Key, out var gatedDataType)
                && !supportedDataTypes.Contains(gatedDataType))
                continue;

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

        if (connectorAttr.Hidden)
        {
            schema["x-hidden"] = true;
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
        var config = await _configurations.FindAsync(connectorName, ct);

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
        var config = await _configurations.RecordHealthAsync(
            connectorName,
            new ConnectorHealthUpdate(
                LastSyncAttempt: lastSyncAttempt,
                LastSuccessfulSync: lastSuccessfulSync,
                LastErrorMessage: lastErrorMessage,
                ClearLastErrorMessage: lastErrorMessage == string.Empty,
                LastErrorAt: lastErrorAt,
                ClearLastErrorAt: lastErrorAt == DateTime.MinValue,
                IsHealthy: isHealthy),
            ct);

        if (config == null)
        {
            _logger.LogWarning(
                "Cannot update health state for connector {ConnectorName}: configuration not found",
                connectorName
            );
            return;
        }

        _logger.LogDebug(
            "Updated health state for connector {ConnectorName}: IsHealthy={IsHealthy}",
            connectorName,
            config.IsHealthy
        );
    }
}
