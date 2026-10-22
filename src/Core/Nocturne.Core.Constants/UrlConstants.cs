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
        /// Base URL of the Nocturne marketing/info website. Temporary path under
        /// the Nightscout Foundation domain until a dedicated domain is provisioned.
        /// </summary>
        public const string NocturneWebsite = "https://nightscoutfoundation.org/nocturne";

        /// <summary>
        /// Base URL for all Nocturne documentation pages.
        /// </summary>
        public const string NocturneDocsBase = NocturneWebsite + "/docs";

        /// <summary>
        /// Setup guide for the Dexcom Share connector.
        /// </summary>
        /// <seealso cref="DataSources.DexcomConnector"/>
        public const string DocsDexcom = NocturneDocsBase + "/connectors/dexcom";

        /// <summary>
        /// Setup guide for the FreeStyle Libre (LibreLinkUp) connector.
        /// </summary>
        /// <seealso cref="DataSources.LibreConnector"/>
        public const string DocsLibre = NocturneDocsBase + "/connectors/libre";

        /// <summary>
        /// Setup guide for the Glooko connector.
        /// </summary>
        /// <seealso cref="DataSources.GlookoConnector"/>
        public const string DocsGlooko = NocturneDocsBase + "/connectors/glooko";
    }
}
