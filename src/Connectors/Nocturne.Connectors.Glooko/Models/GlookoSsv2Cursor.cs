namespace Nocturne.Connectors.Glooko.Models;

/// <summary>
///     Where an SSV2 scan of one resource resumes: the server's <c>updatedAt</c> watermark and
///     tie-break guid, as opaque strings. Kept in the connector's vendor state under the resource path.
/// </summary>
public sealed record GlookoSsv2Cursor(string? LastUpdatedAt, string? LastGuid);
