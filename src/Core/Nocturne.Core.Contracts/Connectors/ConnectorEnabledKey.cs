using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
///     The one owner of how the enabled flag is spelled inside a connector's configuration
///     document, and of what its absence means.
/// </summary>
/// <remarks>
///     A configuration save stores the key as <see cref="Name"/>, whatever casing it was sent in,
///     and so does the toggle (<see cref="With"/>); the reader matches any casing, so a document an
///     older writer spelled <c>Enabled</c> reads as the flag it holds. An absent key reads as
///     enabled, because configurations saved before the flag existed are in use. A stored flag
///     outlives every configuration save (<see cref="CarryOver"/>): only the toggle changes it, and a
///     save sets it only on a row that has none.
/// </remarks>
public static class ConnectorEnabledKey
{
    /// <summary>The spelling every writer stores.</summary>
    public const string Name = "enabled";

    /// <summary>Whether <paramref name="property"/> is the flag, in any casing.</summary>
    public static bool Matches(string property) => property.Equals(Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The flag in <paramref name="configurationJson"/>: true when the key is absent, false when
    ///     the value is not a boolean or the document is not JSON.
    /// </summary>
    public static bool IsEnabled(string configurationJson)
    {
        JsonObject configuration;
        try
        {
            configuration = ParseObject(configurationJson);
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var (key, value) in configuration)
        {
            if (Matches(key))
                return value is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled;
        }

        return true;
    }

    /// <summary><paramref name="configurationJson"/> with the flag set to <paramref name="enabled"/>.</summary>
    public static string With(string configurationJson, bool enabled)
    {
        var configuration = ParseObject(configurationJson);
        RemoveAll(configuration);
        configuration[Name] = enabled;
        return configuration.ToJsonString();
    }

    /// <summary>
    ///     <paramref name="configurationJson"/> with its flag spelled <see cref="Name"/> and holding the
    ///     value in <paramref name="storedConfigurationJson"/>; a stored document with no flag leaves
    ///     the value <paramref name="configurationJson"/> carries, or none when it carries none.
    /// </summary>
    public static string CarryOver(string configurationJson, string storedConfigurationJson)
    {
        var configuration = ParseObject(configurationJson);
        var sent = Flag(configuration);
        RemoveAll(configuration);
        if ((StoredFlag(storedConfigurationJson) ?? sent) is { } flag)
            configuration[Name] = flag.DeepClone();
        return configuration.ToJsonString();
    }

    private static JsonNode? Flag(JsonObject configuration) =>
        configuration.FirstOrDefault(property => Matches(property.Key)).Value;

    private static JsonNode? StoredFlag(string storedConfigurationJson)
    {
        try
        {
            return Flag(ParseObject(storedConfigurationJson));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RemoveAll(JsonObject configuration)
    {
        foreach (var key in configuration.Select(property => property.Key).Where(Matches).ToList())
            configuration.Remove(key);
    }

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json) as JsonObject ?? new JsonObject();
}
