namespace Nocturne.Connectors.FreeStyle.Configurations;

/// <summary>
///     Constants specific to LibreLinkUp/FreeStyle connector
/// </summary>
public static class LibreLinkUpConstants
{
    /// <summary>
    ///     Known LibreLinkUp regional endpoints
    /// </summary>
    public static class Endpoints
    {
        public const string Ae = "api-ae.libreview.io";
        public const string Ap = "api-ap.libreview.io";
        public const string Au = "api-au.libreview.io";
        public const string Ca = "api-ca.libreview.io";
        public const string De = "api-de.libreview.io";
        public const string Eu = "api-eu.libreview.io";
        public const string Eu2 = "api-eu2.libreview.io";
        public const string Fr = "api-fr.libreview.io";
        public const string Jp = "api-jp.libreview.io";
        public const string Us = "api-us.libreview.io";
    }

    /// <summary>
    ///     API endpoints for LibreLinkUp
    /// </summary>
    public static class ApiPaths
    {
        public const string Login = "/llu/auth/login";
        public const string Connections = "/llu/connections";
        public const string GraphData = "/llu/connections/{0}/graph";
    }

    /// <summary>
    ///     The <c>status</c> LibreLinkUp's login answers a refused credential with. The refusal
    ///     arrives as HTTP 200 (<c>{"status":2,"error":{"message":"notAuthenticated"}}</c>), so the
    ///     status line alone reads as a malformed success.
    /// </summary>
    public const int RejectedCredentialStatus = 2;

    /// <summary>
    ///     The <c>status</c> LibreLinkUp's login answers with, as HTTP 200, when the account must act
    ///     in the LibreLinkUp app first — accept new terms of use or a privacy policy
    ///     (<c>{"status":4,"data":{"step":{"type":"tou",...}}}</c>). No retry clears it; only the
    ///     account holder can, like <see cref="RejectedCredentialStatus"/>.
    /// </summary>
    public const int AccountActionRequiredStatus = 4;

    /// <summary>
    ///     Configuration specific to LibreLinkUp
    /// </summary>
    public static class Configuration
    {
        public const string DefaultRegion = "EU";
        public const string DeviceIdentifier = "libre-connector";
        public const string EntryType = "sgv";
    }
}