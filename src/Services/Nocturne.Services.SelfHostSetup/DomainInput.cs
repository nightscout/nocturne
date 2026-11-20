using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace Nocturne.Services.SelfHostSetup;

/// <summary>
/// Reads hostnames and tokens the way people paste them: with a scheme, a trailing slash or dot,
/// stray whitespace, in any case.
/// </summary>
public static partial class DomainInput
{
    private const string DuckDnsSuffix = ".duckdns.org";

    public static bool TryParseDuckDnsName(string? input, [NotNullWhen(true)] out string? name)
    {
        name = Normalise(input);
        if (name is not null && name.EndsWith(DuckDnsSuffix, StringComparison.Ordinal))
            name = name[..^DuckDnsSuffix.Length];

        if (name is null || !Label().IsMatch(name))
        {
            name = null;
            return false;
        }

        return true;
    }

    public static bool TryParseDuckDnsToken(string? input, [NotNullWhen(true)] out string? token)
    {
        token = input?.Trim().ToLowerInvariant();
        if (token is null || !DuckDnsToken().IsMatch(token))
        {
            token = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Nocturne derives the passkey relying party and the tenant hosts from the base domain, so it
    /// is a bare hostname of two or more labels: never an address, a port or a path.
    /// </summary>
    public static bool TryParseBaseDomain(string? input, [NotNullWhen(true)] out string? domain)
    {
        domain = Normalise(input);
        if (domain is null
            || IPAddress.TryParse(domain, out _)
            || domain.Split('.') is not { Length: >= 2 } labels
            || !labels.All(Label().IsMatch))
        {
            domain = null;
            return false;
        }

        return true;
    }

    private static string? Normalise(string? input)
    {
        var value = input?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
            return null;

        foreach (var scheme in (string[])["https://", "http://"])
        {
            if (value.StartsWith(scheme, StringComparison.Ordinal))
                value = value[scheme.Length..];
        }

        return value.TrimEnd('/').TrimEnd('.');
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Label();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex DuckDnsToken();
}
