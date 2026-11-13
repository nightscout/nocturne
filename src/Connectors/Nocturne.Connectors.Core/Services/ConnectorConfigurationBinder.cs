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

            if (TryConvert(element, property.PropertyType, out var value))
                property.SetValue(config, value);
        }
    }

    /// <summary>
    ///     Converts <paramref name="element"/> to <paramref name="targetType"/>, the one conversion
    ///     binding and save validation share. An enum is read by member name, as the schema lists it.
    /// </summary>
    public static bool TryConvert(JsonElement element, Type targetType, out object? value)
    {
        value = null;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type == typeof(string) && element.ValueKind == JsonValueKind.String)
            value = element.GetString();
        else if (type == typeof(int) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var integer))
            value = integer;
        else if (type == typeof(double) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var number))
            value = number;
        else if (type == typeof(bool)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            value = element.GetBoolean();
        else if (type.IsEnum && element.ValueKind == JsonValueKind.String
            && Enum.GetNames(type).Contains(element.GetString()))
            value = Enum.Parse(type, element.GetString()!);

        return value is not null;
    }

    /// <summary>
    ///     The keys of <paramref name="root"/> in <paramref name="bound"/> whose value
    ///     <see cref="TryConvert"/> refuses. A key not in the map is not reported.
    /// </summary>
    public static IReadOnlyList<string> UnbindableKeys(IReadOnlyDictionary<string, Type> bound, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];

        return root.EnumerateObject()
            .Select(property => (Key: ConnectorEnabledKey.Matches(property.Name) ? "enabled" : property.Name, property.Value))
            .Where(entry => bound.TryGetValue(entry.Key, out var type) && !TryConvert(entry.Value, type, out _))
            .Select(entry => entry.Key)
            .ToList();
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
    ///     The keys <see cref="ApplyJsonToConfig{TConfig}"/> binds on <paramref name="configType"/>,
    ///     each with its property type, whether or not the schema lists it. The enabled flag is in the
    ///     map only as <c>enabled</c>; <see cref="ConnectorEnabledKey"/> matches its other casings.
    /// </summary>
    public static IReadOnlyDictionary<string, Type> BoundKeyTypes(Type configType) =>
        configType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(GetBoundKeyName, p => p.PropertyType, StringComparer.Ordinal);

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

