using System.Reflection;
using System.Text.Json;
using System.Linq;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Applies JSON and secret values to connector configuration objects via reflection.
///     Extracted from ConnectorSyncExecutor for reuse by ConnectorConfigurationLoader.
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
            var camelName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (!root.TryGetProperty(camelName, out var element))
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
            var camelName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (secrets.TryGetValue(camelName, out var value))
                property.SetValue(config, value);
        }
    }
}
