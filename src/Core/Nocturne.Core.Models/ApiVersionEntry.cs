namespace Nocturne.Core.Models;

/// <summary>
/// One entry of the <c>/api/versions</c> array: an API level and the path it is served under.
/// </summary>
/// <param name="Version">Semantic version of the API level.</param>
/// <param name="Url">Path the API level is mounted at, such as <c>/api/v3</c>.</param>
/// <seealso cref="VersionResponse"/>
public record ApiVersionEntry(string Version, string Url);
