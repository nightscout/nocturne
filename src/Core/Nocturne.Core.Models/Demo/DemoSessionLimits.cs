namespace Nocturne.Core.Models.Demo;

/// <summary>
/// Bounds on the sessions held by a demo tenant's shared visitor account.
/// </summary>
/// <remarks>
/// In Core rather than beside the demo service because the cap is enforced in the data layer, at
/// the single place a <c>refresh_tokens</c> row is created — the demo sign-in endpoint is only one
/// of the paths that reaches it, and the anonymous, unrate-limited refresh endpoint rotates rows
/// through the same sink without passing through any demo code.
/// <para>
/// The per-IP rate limit on the sign-in endpoint is not a substitute. That partition key comes from
/// <c>Connection.RemoteIpAddress</c>, which <c>UseForwardedHeaders</c> — running before the rate
/// limiter, with <c>KnownProxies</c> and <c>KnownIPNetworks</c> cleared because the API is only
/// meant to be reachable through the gateway — takes from <c>X-Forwarded-For</c>. The gateway does
/// not strip that header, so a caller rotating it gets a fresh partition per request and the limit
/// bounds nothing. It is kept for the friction it adds to naive abuse; this cap is the actual
/// ceiling, and it is enforced on a value no caller supplies.
/// </para>
/// </remarks>
public static class DemoSessionLimits
{
    /// <summary>
    /// Ceiling on live <c>refresh_tokens</c> rows for the demo subject.
    /// </summary>
    /// <remarks>
    /// Sized for concurrent real visitors, not for one visitor: the account is shared, so a trimmed
    /// session belongs to someone who has almost certainly left. Reaching the cap displaces the
    /// oldest rather than refusing the newest, because refusing would make the demo unusable for
    /// everyone as soon as it filled — and displacing costs little, because the access token is a
    /// self-contained JWT with no revocation check. A visitor whose refresh row is trimmed is not
    /// signed out mid-page; they lose only their next refresh, at which point the login page signs
    /// them straight back in.
    /// </remarks>
    public const int MaxLiveSessions = 50;
}
