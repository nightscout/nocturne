using Nocturne.API.Multitenancy;

namespace Nocturne.API.Extensions;

public static class ReturnUrlExtensions
{
    /// <summary>
    /// Whether a caller-supplied return URL is safe to redirect a browser to after a sign-in.
    /// </summary>
    public static bool IsValidReturnUrl(this BaseDomainOptions baseDomain, string returnUrl)
    {
        // Site-local path: starts with "/" but not "//" or "/\", which browsers
        // resolve as scheme-relative — "Location: //evil.com" leaves the site.
        if (returnUrl.StartsWith('/'))
        {
            return returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\');
        }

        // Absolute URL: parse and compare scheme + authority against the public
        // origin, so neither "https://example.com.evil.com" (prefix) nor
        // "https://example.com@evil.com" (userinfo) can pass a string match.
        var origin = baseDomain.PublicOrigin;
        return !string.IsNullOrEmpty(origin)
            && Uri.TryCreate(returnUrl, UriKind.Absolute, out var target)
            && Uri.TryCreate(origin, UriKind.Absolute, out var expected)
            && target.Scheme == expected.Scheme
            && string.Equals(target.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(target.UserInfo);
    }
}
