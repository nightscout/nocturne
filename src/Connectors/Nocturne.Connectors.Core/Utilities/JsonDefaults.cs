using System.Text.Json;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     Shared JSON serialization options for consistent deserialization across all connectors.
///     Using case-insensitive property matching improves resilience against API changes.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    ///     Default options for deserializing API responses.
    ///     Case-insensitive to handle varying API response formats.
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Options for serializing data with camelCase property names.
    ///     Useful for sending data to APIs that expect camelCase.
    /// </summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Options for serializing data with indentation for debugging/logging.
    /// </summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}