using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Base implementation of connector configuration with common properties
/// </summary>
public abstract class BaseConnectorConfiguration : IConnectorConfiguration
{
    private static readonly ConcurrentDictionary<Type, Dictionary<SyncDataType, PropertyInfo>>
        SyncTogglePropertyCache = new();

    /// <summary>
    ///     Gets the connector name from the ConnectorRegistration attribute.
    ///     Used for error messages and logging.
    /// </summary>
    private string ConnectorName =>
        GetType().GetCustomAttribute<ConnectorRegistrationAttribute>()?.ConnectorName
        ?? GetType().Name.Replace("Configuration", "");

    /// <summary>
    ///     Gets the environment variable prefix from the ConnectorRegistration attribute.
    /// </summary>
    private string? EnvPrefix =>
        GetType().GetCustomAttribute<ConnectorRegistrationAttribute>()?.EnvironmentPrefix;
    /// <summary>
    ///     Timezone offset in hours (default 0).
    ///     Can be set via environment variable: CONNECT_{CONNECTORNAME}_TIMEZONE_OFFSET
    ///     or appsettings: {Configuration}:TimezoneOffset
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.TimezoneOffset, MinValue = -12, MaxValue = 14)]
    public double TimezoneOffset { get; set; } = 0;

    [Required] public ConnectSource ConnectSource { get; set; }

    /// <summary>
    ///     Whether the connector is enabled and should sync data.
    ///     When disabled, the connector enters standby mode.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Enabled)]
    public bool Enabled { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.MaxRetryAttempts, MinValue = 0, MaxValue = 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [ConnectorProperty(ConnectorPropertyKey.BatchSize, MinValue = 1, MaxValue = 500)]
    public int BatchSize { get; set; } = 50;

    [ConnectorProperty(ConnectorPropertyKey.SyncIntervalMinutes, MinValue = 1, MaxValue = 60)]
    public int SyncIntervalMinutes { get; set; } = 5;

    [ConnectorProperty(ConnectorPropertyKey.GlucoseProcessing)]
    public GlucoseProcessing GlucoseProcessing { get; set; } = GlucoseProcessing.Smoothed;

    [ConnectorProperty(ConnectorPropertyKey.SyncGlucose, DefaultValue = "true")]
    public bool SyncGlucose { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncManualBG, DefaultValue = "true")]
    public bool SyncManualBG { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncBoluses, DefaultValue = "true")]
    public bool SyncBoluses { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncBasalInjections, DefaultValue = "true")]
    public bool SyncBasalInjections { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncCarbIntake, DefaultValue = "true")]
    public bool SyncCarbIntake { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncBolusCalculations, DefaultValue = "true")]
    public bool SyncBolusCalculations { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncNotes, DefaultValue = "true")]
    public bool SyncNotes { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncDeviceEvents, DefaultValue = "true")]
    public bool SyncDeviceEvents { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncStateSpans, DefaultValue = "true")]
    public bool SyncStateSpans { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncTempBasals, DefaultValue = "true")]
    public bool SyncTempBasals { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncProfiles, DefaultValue = "true")]
    public bool SyncProfiles { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncDeviceStatus, DefaultValue = "true")]
    public bool SyncDeviceStatus { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncActivity, DefaultValue = "true")]
    public bool SyncActivity { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncFood, DefaultValue = "true")]
    public bool SyncFood { get; set; } = true;

    /// <summary>
    ///     Override for active threshold (minutes). 0 = use connector default.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.ActiveThresholdMinutes)]
    public int ActiveThresholdMinutes { get; set; } = 0;

    /// <summary>
    ///     Override for stale threshold (minutes). 0 = use connector default.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.StaleThresholdMinutes)]
    public int StaleThresholdMinutes { get; set; } = 0;

    /// <summary>
    ///     Whether this configuration has <paramref name="type"/> switched on. A data type with no
    ///     sync toggle property — Calibrations and BGChecks — has nothing to switch it off, so it
    ///     syncs whenever the connector declares it supported.
    /// </summary>
    public bool IsDataTypeEnabled(SyncDataType type)
        => !SyncToggleProperties(GetType()).TryGetValue(type, out var toggle)
           || (bool)toggle.GetValue(this)!;

    public List<SyncDataType> GetEnabledDataTypes(List<SyncDataType> supportedTypes)
        => supportedTypes.Where(IsDataTypeEnabled).ToList();

    /// <summary>
    ///     The bool properties carrying a sync toggle key on a concrete configuration type, indexed
    ///     by the data type each one gates.
    /// </summary>
    private static Dictionary<SyncDataType, PropertyInfo> SyncToggleProperties(Type configurationType)
        => SyncTogglePropertyCache.GetOrAdd(configurationType, static type =>
        {
            var toggles = new Dictionary<SyncDataType, PropertyInfo>();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var key = property.GetCustomAttribute<ConnectorPropertyAttribute>()?.Key;

                if (key is not null && ConnectorSyncToggles.ByPropertyKey.TryGetValue(key.Value, out var dataType))
                    toggles[dataType] = property;
            }

            return toggles;
        });

    public virtual void Validate()
    {
        if (!Enum.IsDefined(typeof(ConnectSource), ConnectSource))
            throw new ArgumentException($"Invalid connector source: {ConnectSource}");

        if (MaxRetryAttempts < 0)
            throw new ArgumentException("MaxRetryAttempts cannot be negative");

        if (BatchSize <= 0)
            throw new ArgumentException("BatchSize must be greater than zero");

        // Validate properties marked with [Required] or [ConnectorProperty(Required = true)]
        ValidateRequiredProperties();

        // Allow derived classes to add additional validation
        ValidateSourceSpecificConfiguration();
    }

    /// <summary>
    ///     Whether every required property — those marked [Required] or
    ///     [ConnectorProperty(Required = true)] — has a value. Required secrets are merged into the
    ///     configuration before this is evaluated, so a connector that is enabled but missing its
    ///     credentials (e.g. enabled via the UI toggle, or saved before secrets were entered, or a
    ///     required secret later removed) reports false here and must not sync — otherwise it polls
    ///     every cycle with empty credentials and fails authentication forever.
    /// </summary>
    public bool HasRequiredConfiguration() => MissingRequiredProperties().Count == 0;

    /// <summary>
    ///     The display names of the required properties that have no value, so a caller can say which
    ///     ones are missing rather than only that something is.
    /// </summary>
    public IReadOnlyList<string> MissingRequiredProperties()
    {
        var missing = new List<string>();
        foreach (var (property, displayName, _) in GetRequiredProperties())
        {
            if (IsRequiredValueMissing(property, property.GetValue(this)))
                missing.Add(displayName);
        }

        return missing;
    }

    /// <summary>
    ///     Validates all properties marked with [Required] attribute or
    ///     [ConnectorProperty(Required = true)].
    ///     Throws ArgumentException if any required string property is null or empty.
    /// </summary>
    private void ValidateRequiredProperties()
    {
        foreach (var (property, displayName, connectorProp) in GetRequiredProperties())
        {
            var value = property.GetValue(this);
            if (!IsRequiredValueMissing(property, value))
                continue;

            if (property.PropertyType == typeof(string))
            {
                var envVarHint = connectorProp != null && EnvPrefix != null
                    ? $" (set via {connectorProp.GetFullEnvVarName(EnvPrefix)} or configuration)"
                    : "";
                throw new ArgumentException(
                    $"{ConnectorName}: {displayName} is required{envVarHint}");
            }

            throw new ArgumentException(
                $"{ConnectorName}: {displayName} is required");
        }
    }

    /// <summary>
    ///     Enumerates the properties required via [Required] or [ConnectorProperty(Required = true)],
    ///     with their display name and connector-property attribute (if any). Shared by
    ///     <see cref="HasRequiredConfiguration"/> and <see cref="ValidateRequiredProperties"/> so
    ///     both agree on what "required" means.
    /// </summary>
    private IEnumerable<(PropertyInfo Property, string DisplayName, ConnectorPropertyAttribute? ConnectorProp)>
        GetRequiredProperties()
    {
        foreach (var property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string? displayName = null;

            if (property.GetCustomAttribute<RequiredAttribute>() != null)
                displayName = property.Name;

            var connectorProp = property.GetCustomAttribute<ConnectorPropertyAttribute>();
            if (connectorProp is { Required: true })
                displayName = connectorProp.GetKeyName();

            if (displayName != null)
                yield return (property, displayName, connectorProp);
        }
    }

    /// <summary>
    ///     Whether a required property's value counts as missing: null/whitespace for strings,
    ///     null for nullable value types. Non-nullable value types always carry a value.
    /// </summary>
    private static bool IsRequiredValueMissing(PropertyInfo property, object? value)
    {
        if (property.PropertyType == typeof(string))
            return string.IsNullOrWhiteSpace(value as string);

        if (Nullable.GetUnderlyingType(property.PropertyType) != null)
            return value == null;

        return false;
    }

    /// <summary>
    ///     Override this method to add connector-specific validation beyond [Required] properties.
    ///     The base implementation does nothing - derived classes can add custom validation rules.
    /// </summary>
    protected virtual void ValidateSourceSpecificConfiguration()
    {
        // Default implementation: no additional validation needed
        // Derived classes can override to add custom validation
    }
}
