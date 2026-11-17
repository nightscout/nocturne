namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
/// Glooko refused a request because of the cached session rather than the request, so repeating the
/// request unchanged cannot succeed. The sync invalidates the cached session, signs in again and
/// retries the pass once.
/// </summary>
public abstract class GlookoSessionRejectedException : Exception
{
    protected GlookoSessionRejectedException(string message) : base(message) { }
}

/// <summary>
/// Glooko no longer accepts the session cookie: a 401, or a 200 carrying the HTML sign-in page it
/// serves an expired session in place of JSON. The session is cached for
/// <see cref="Configurations.GlookoConstants.SessionLifetime"/>, which Glooko does not promise to
/// honour, so without this every run reuses the dead cookie until the cache entry expires.
/// </summary>
public sealed class GlookoSessionExpiredException : GlookoSessionRejectedException
{
    public GlookoSessionExpiredException(string message) : base(message) { }
}
