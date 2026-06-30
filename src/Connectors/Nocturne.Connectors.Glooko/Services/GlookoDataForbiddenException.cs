namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
/// Thrown when Glooko returns 403 Forbidden (e.g. <c>data_cant_view</c>) on a patient-scoped
/// data endpoint. The patient code (<c>glookoCode</c>) is baked into the request URL and can change
/// when the account or its data source is re-linked, so retrying the same URL is futile — the sync
/// must invalidate the cached session and re-authenticate to resolve the current code.
/// </summary>
public sealed class GlookoDataForbiddenException : Exception
{
    public GlookoDataForbiddenException(string message) : base(message) { }
}
