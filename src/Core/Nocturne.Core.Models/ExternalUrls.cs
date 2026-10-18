namespace Nocturne.Core.Models;

/// <summary>
/// External URLs used by Nocturne for documentation, website, and other public resources.
/// Exposed via API so the frontend can consume these from a single source of truth
/// rather than hard-coding URLs in client code.
/// </summary>
/// <seealso cref="ConnectorDocsUrls"/>
public class ExternalUrls
{
    /// <summary>
    /// Main Nocturne website URL
    /// </summary>
    public required string Website { get; init; }

    /// <summary>
    /// Base URL for Nocturne documentation
    /// </summary>
    public required string DocsBase { get; init; }

    /// <summary>
    /// Documentation URLs for connectors
    /// </summary>
    public required ConnectorDocsUrls ConnectorDocs { get; init; }
}

/// <summary>
/// Documentation URLs for each connector type
/// </summary>
public class ConnectorDocsUrls
{
    public required string Dexcom { get; init; }

    /// <summary>
    /// FreeStyle Libre connector documentation
    /// </summary>
    public required string Libre { get; init; }

    /// <summary>
    /// Medtronic CareLink connector documentation
    /// </summary>
    public required string CareLink { get; init; }

    /// <summary>
    /// Nightscout bridge connector documentation
    /// </summary>
    public required string Nightscout { get; init; }

    public required string Glooko { get; init; }
}
