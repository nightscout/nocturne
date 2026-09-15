using System.Collections.Frozen;

namespace Nocturne.Connectors.Dexcom.Configurations;

/// <summary>
///     Constants specific to Dexcom Share connector
/// </summary>
public static class DexcomConstants
{
    /// <summary>
    ///     Known Dexcom Share servers
    /// </summary>
    public static class Servers
    {
        public const string Us = "share2.dexcom.com";
        public const string Ous = "shareous1.dexcom.com";
    }

    /// <summary>
    ///     Dexcom Share reports a refused credential as HTTP 500 carrying one of these in the
    ///     <c>Code</c> field, so the status alone reads as a server fault worth retrying. Repeating
    ///     the request is what earns <c>SSO_AuthenticateMaxAttemptsExceeded</c>.
    /// </summary>
    public static readonly FrozenSet<string> RejectedCredentialCodes = new[]
    {
        "AccountPasswordInvalid",
        "SSO_AuthenticateAccountNotFound",
        "SSO_AuthenticatePasswordInvalid",
        "SSO_AuthenticateMaxAttemptsExceeded",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}