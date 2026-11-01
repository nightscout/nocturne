using System.Reflection;
using System.Text.Json;
using System.Linq;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Applies JSON and secret values to connector configuration objects via reflection.
///     Uses <see cref="ConnectorPropertyAttribute.GetKeyName"/> when present so that
///     binding matches the attribute key used by schema generation and configuration storage.
/// </summary>
public static class ConnectorConfigurationBinder
{
    public static void ApplyJsonToConfig<TConfig>(JsonDocument configuration, TConfig config)
        where TConfig : class
    {
        var properties = config.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var root = configuration.RootElement;

        foreach (var property in properties.Where(p => p.CanWrite))
        {
            var camelName = GetBoundKeyName(property);
            if (!TryGetValue(root, camelName, out var element))
                continue;

            try
            {
                if (property.PropertyType == typeof(string)
                    && element.ValueKind == JsonValueKind.String)
                    property.SetValue(config, element.GetString());
                else if (property.PropertyType == typeof(int)
                    && element.ValueKind == JsonValueKind.Number)
                    property.SetValue(config, element.GetInt32());
                else if (property.PropertyType == typeof(double)
                    && element.ValueKind == JsonValueKind.Number)
                    property.SetValue(config, element.GetDouble());
                else if (property.PropertyType == typeof(bool)
                    && (element.ValueKind == JsonValueKind.True
                        || element.ValueKind == JsonValueKind.False))
                    property.SetValue(config, element.GetBoolean());
            }
            catch (TargetInvocationException)
            {
                // Skip properties that can't be set (e.g. setter throws)
            }
            catch (ArgumentException)
            {
                // Skip properties with type mismatches
            }
        }
    }

    public static void ApplySecretsToConfig<TConfig>(
        Dictionary<string, string> secrets, TConfig config)
        where TConfig : class
    {
        var properties = config.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties.Where(p => p.CanWrite && p.PropertyType == typeof(string)))
        {
            var camelName = GetBoundKeyName(property);
            if (secrets.TryGetValue(camelName, out var value))
                property.SetValue(config, value);
        }
    }

    /// <summary>
    ///     The keys under which <paramref name="configType"/>'s secret properties are stored.
    /// </summary>
    public static IReadOnlySet<string> SecretKeyNames(Type configType) =>
        configType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<ConnectorPropertyAttribute>()?.Secret == true)
            .Select(GetBoundKeyName)
            .ToHashSet(StringComparer.Ordinal);

    private static bool TryGetValue(JsonElement root, string key, out JsonElement value)
    {
        if (!ConnectorEnabledKey.Matches(key))
            return root.TryGetProperty(key, out value);

        foreach (var property in root.EnumerateObject())
        {
            if (!ConnectorEnabledKey.Matches(property.Name))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static string GetBoundKeyName(PropertyInfo property)
    {
        var keyName = property.GetCustomAttribute<ConnectorPropertyAttribute>()?.GetKeyName()
            ?? property.Name;
        return char.ToLowerInvariant(keyName[0]) + keyName[1..];
    }
}

