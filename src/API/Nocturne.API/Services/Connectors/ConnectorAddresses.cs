using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Services;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// The free-text addresses of a connector's configuration: the properties
/// <see cref="DelegatedConfigurationGuard.ChoosesDestination"/> picks, which its schema marks
/// <see cref="SchemaMarker"/>. The path or query of an address can be the credential
/// (<see cref="NightscoutAddress"/>), so a client is given only its
/// <see cref="NightscoutAddress.Shown"/> form and has no stored value to send back. A save keeps
/// the stored address unless it names a new one: an address it leaves out, or a required one it
/// sends blank, is the stored one. A blank optional address clears it. The sent value is never
/// compared with the shown form, as every path on one host shows the same.
/// </summary>
public sealed class ConnectorAddresses
{
    /// <summary>The schema keyword that marks a property as an address.</summary>
    public const string SchemaMarker = "x-address";

    private readonly IReadOnlyList<(string Key, bool Required)> _addresses;

    private ConnectorAddresses(IReadOnlyList<(string Key, bool Required)> addresses) => _addresses = addresses;

    /// <summary>
    /// The addresses <paramref name="schema"/>, a connector's configuration schema, marks, each
    /// required when <paramref name="required"/>, the schema's <c>required</c> list, names it.
    /// </summary>
    public static ConnectorAddresses Of(JsonDocument schema, IReadOnlySet<string> required)
    {
        var root = schema.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return new ConnectorAddresses([]);

        return new ConnectorAddresses(properties.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty(SchemaMarker, out var marker)
                && marker.ValueKind == JsonValueKind.True)
            .Select(property => (property.Name, required.Contains(property.Name)))
            .ToList());
    }

    /// <summary><paramref name="configuration"/> with each address it holds in its shown form.</summary>
    public JsonDocument Shown(JsonDocument configuration)
    {
        if (_addresses.Count == 0 || JsonNode.Parse(configuration.RootElement.GetRawText()) is not JsonObject document)
            return configuration;

        foreach (var (key, _) in _addresses)
        {
            if (document[key] is JsonValue value && value.TryGetValue<string>(out var address)
                && !string.IsNullOrWhiteSpace(address))
                document[key] = NightscoutAddress.Shown(address);
        }

        return JsonDocument.Parse(document.ToJsonString());
    }

    /// <summary>
    /// <paramref name="proposed"/> with each address it leaves out, and each required one it sends
    /// blank, taken from <paramref name="stored"/>.
    /// </summary>
    public JsonDocument WithStored(JsonDocument proposed, JsonDocument? stored)
    {
        if (_addresses.Count == 0
            || stored is not { RootElement.ValueKind: JsonValueKind.Object } storedDocument
            || JsonNode.Parse(proposed.RootElement.GetRawText()) is not JsonObject document)
            return proposed;

        var kept = false;
        foreach (var (key, required) in _addresses)
        {
            if (!storedDocument.RootElement.TryGetProperty(key, out var storedAddress))
                continue;

            var keeps = !document.TryGetPropertyValue(key, out var sent)
                || (required && IsBlank(sent));
            if (!keeps)
                continue;

            document[key] = JsonNode.Parse(storedAddress.GetRawText());
            kept = true;
        }

        return kept ? JsonDocument.Parse(document.ToJsonString()) : proposed;
    }

    private static bool IsBlank(JsonNode? value) =>
        value is null || (value is JsonValue text && text.TryGetValue<string>(out var address) && string.IsNullOrWhiteSpace(address));
}
