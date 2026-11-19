namespace Nocturne.Core.Constants;

/// <summary>
/// External URLs referenced throughout the Nocturne application for documentation links,
/// help pages, and connector setup guides.
/// </summary>
/// <seealso cref="DataSources"/>
/// <seealso cref="ConnectorEnvironmentVariables"/>
public static class UrlConstants
{
    /// <summary>
    /// External-facing URLs for the Nocturne website and documentation.
    /// </summary>
    public static class External
    {
        /// <summary>
        /// Base URL of the Nocturne website.
        /// </summary>
        public const string NocturneWebsite = "https://getnocturne.dev";

        /// <summary>
        /// Base URL for all Nocturne documentation pages.
        /// </summary>
        public const string NocturneDocsBase = NocturneWebsite + "/docs";

        /// <summary>
        /// Setup guide for the Google Health connector.
        /// </summary>
        /// <seealso cref="DataSources.GoogleHealthConnector"/>
        public const string DocsGoogleHealth = NocturneDocsBase + "/data-sources/google-health";

        /// <summary>
        /// Setup guide for the Oura connector.
        /// </summary>
        /// <seealso cref="DataSources.OuraConnector"/>
        public const string DocsOura = NocturneDocsBase + "/data-sources/oura";
    }
}
