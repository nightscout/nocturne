namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Timing shared by refresh-token rotation and the store that keeps its rows.
/// </summary>
public static class RefreshTokenRotation
{
    /// <summary>
    /// Window during which reuse of a just-rotated refresh token is treated as a benign
    /// concurrent-request race rather than token theft. An SSR web app fans out several
    /// parallel requests per navigation (page load, preload, proxied API and remote-function
    /// calls) that all carry the same refresh-token cookie, so a client can legitimately
    /// present a token that another in-flight request rotated moments earlier.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(60);
}
