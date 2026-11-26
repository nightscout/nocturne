using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing.Patterns;
using Nocturne.API.Attributes;

namespace Nocturne.API.Configuration;

/// <summary>
/// Replaces each <see cref="CredentialRouteValueAttribute"/> segment of a request path with its
/// route parameter name, e.g. <c>/api/v2/authorization/request/{accessToken}</c>, and the value of
/// a <c>token</c>, <c>secret</c> or <c>access_token</c> query parameter with the parameter name, as
/// clients authenticate with <c>?token=</c>, <c>?secret=</c> and, on SignalR, <c>?access_token=</c>.
/// </summary>
/// <remarks>
/// Paths are matched against the routes themselves rather than the matched endpoint, because the
/// hosting log scope and request lines capture the path before routing runs. A parameter segment
/// matches any value and constraints are ignored, so a path that also fits another route, such as
/// <c>DELETE api/v4/alert-invites/{id:guid}</c>, is redacted too. The routes are read in
/// <see cref="Load"/>, once every endpoint is mapped.
/// </remarks>
public sealed class CredentialPathRedactor : IRequestPathRedactor
{
    public const string CredentialRouteMessage = "The path holds a Nocturne credential, which is never sent to another host.";

    private static readonly string[] CredentialQueryParameters = ["token", "secret", "access_token"];

    private static readonly Uri ResolutionBase = new("http://localhost/");

    private volatile CredentialRoute[] _routes = [];

    public void Load(IEnumerable<Endpoint> endpoints)
    {
        _routes = endpoints
            .OfType<RouteEndpoint>()
            .Select(CredentialRoute.From)
            .OfType<CredentialRoute>()
            .ToArray();
    }

    public bool TryRedact(string path, [NotNullWhen(true)] out string? redacted)
    {
        redacted = null;
        if (!path.StartsWith('/') && !path.StartsWith('?'))
        {
            return false;
        }

        var queryStart = path.IndexOf('?');
        var pathPart = queryStart < 0 ? path : path[..queryStart];
        var query = queryStart < 0 ? "" : path[queryStart..];

        var redactedPath = RedactRoute(pathPart);
        var redactedQuery = RedactQuery(query);
        if (redactedPath is null && redactedQuery is null)
        {
            return false;
        }

        redacted = (redactedPath ?? pathPart) + (redactedQuery ?? query);
        return true;
    }

    /// <summary>
    /// <paramref name="pathAndQuery"/> without the query parameters that carry the caller's own
    /// credential, so it can be sent to a host that is not Nocturne. Other parameters keep their
    /// order and encoding.
    /// </summary>
    public static string WithoutCredentialQuery(string pathAndQuery)
    {
        var queryStart = pathAndQuery.IndexOf('?');
        if (queryStart < 0)
        {
            return pathAndQuery;
        }

        var kept = pathAndQuery[(queryStart + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => CredentialQueryName(pair.AsSpan(0, NameLength(pair))) is null)
            .ToArray();

        return kept.Length == 0
            ? pathAndQuery[..queryStart]
            : string.Concat(pathAndQuery.AsSpan(0, queryStart + 1), string.Join('&', kept));
    }

    private static int NameLength(string pair)
    {
        var equals = pair.IndexOf('=');
        return equals < 0 ? pair.Length : equals;
    }

    /// <summary>
    /// Whether <paramref name="pathAndQuery"/>, resolved as <see cref="Uri"/> resolves a relative
    /// path (dot segments, backslashes and escapes) and with empty segments dropped, names a route
    /// with a <see cref="CredentialRouteValueAttribute"/> segment. Nocturne minted that credential,
    /// so no other host has a use for it and the request is never sent to one. A path that does not
    /// resolve counts as one, as its target host may resolve it differently.
    /// </summary>
    public bool CarriesCredentialRoute(string pathAndQuery) =>
        !Uri.TryCreate(ResolutionBase, pathAndQuery.TrimStart('/'), out var resolved)
        || MatchingRoute(string.Join('/', resolved.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)))
            is not null;

    private string? RedactRoute(string path)
    {
        if (MatchingRoute(path) is not { } route)
        {
            return null;
        }

        return string.Concat(
            "/",
            route.Redact(path.AsSpan().Trim('/').ToString().Split('/')),
            path.Length > 1 && path.EndsWith('/') ? "/" : ""
        );
    }

    private CredentialRoute? MatchingRoute(ReadOnlySpan<char> path)
    {
        var routes = _routes;
        if (routes.Length == 0 || path.IsEmpty)
        {
            return null;
        }

        var trimmed = path.Trim('/');
        foreach (var route in routes)
        {
            if (route.Matches(trimmed))
            {
                return route;
            }
        }

        return null;
    }

    /// <summary>
    /// Names match after URL decoding, and the whole query is scanned because a server-side query
    /// has no fragment.
    /// </summary>
    private static string? RedactQuery(string query)
    {
        if (query.Length == 0)
        {
            return null;
        }

        StringBuilder? builder = null;
        var copied = 0;
        var position = 1;
        while (position <= query.Length)
        {
            var next = query.IndexOf('&', position);
            var pairEnd = next < 0 ? query.Length : next;
            var pair = query.AsSpan(position, pairEnd - position);
            var equals = pair.IndexOf('=');
            if (equals >= 0
                && pair.Length > equals + 1
                && CredentialQueryName(pair[..equals]) is { } name)
            {
                var valueStart = position + equals + 1;
                builder ??= new StringBuilder(query.Length);
                builder.Append(query, copied, valueStart - copied).Append('{').Append(name).Append('}');
                copied = pairEnd;
            }

            if (next < 0)
            {
                break;
            }

            position = next + 1;
        }

        return builder?.Append(query, copied, query.Length - copied).ToString();
    }

    private static string? CredentialQueryName(ReadOnlySpan<char> encodedName)
    {
        var name = encodedName.IndexOfAny('%', '+') < 0
            ? encodedName.ToString()
            : Uri.UnescapeDataString(encodedName.ToString().Replace('+', ' '));
        foreach (var credential in CredentialQueryParameters)
        {
            if (name.Equals(credential, StringComparison.OrdinalIgnoreCase))
            {
                return credential;
            }
        }

        return null;
    }

    /// <param name="Segments">A literal, or null for a parameter.</param>
    /// <param name="CredentialNames">The parameter name at each credential segment, else null.</param>
    private sealed record CredentialRoute(string?[] Segments, string?[] CredentialNames)
    {
        public static CredentialRoute? From(RouteEndpoint endpoint)
        {
            var credentials = CredentialParameterNames(endpoint);
            if (credentials.Count == 0)
            {
                return null;
            }

            var pattern = endpoint.RoutePattern;
            var segments = new string?[pattern.PathSegments.Count];
            var names = new string?[pattern.PathSegments.Count];
            for (var i = 0; i < segments.Length; i++)
            {
                switch (pattern.PathSegments[i].Parts)
                {
                    case [RoutePatternLiteralPart literal]:
                        segments[i] = literal.Content;
                        break;
                    case [RoutePatternParameterPart { IsCatchAll: false, IsOptional: false } parameter]:
                        names[i] = credentials.Remove(parameter.Name) ? parameter.Name : null;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Route '{pattern.RawText}' carries a credential but has a segment the "
                            + "redactor cannot match; give each segment one literal or one required parameter."
                        );
                }
            }

            if (credentials.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Route '{pattern.RawText}' marks {string.Join(", ", credentials)} with "
                    + $"{nameof(CredentialRouteValueAttribute)}, but the route has no such parameter."
                );
            }

            return new CredentialRoute(segments, names);
        }

        public bool Matches(ReadOnlySpan<char> path)
        {
            var i = 0;
            foreach (var range in path.Split('/'))
            {
                if (i == Segments.Length)
                {
                    return false;
                }

                var segment = path[range];
                if (Segments[i] is { } literal
                    ? !segment.Equals(literal, StringComparison.OrdinalIgnoreCase)
                    : segment.IsEmpty)
                {
                    return false;
                }

                i++;
            }

            return i == Segments.Length;
        }

        public string Redact(string[] path)
        {
            for (var i = 0; i < path.Length; i++)
            {
                if (CredentialNames[i] is { } name)
                {
                    path[i] = $"{{{name}}}";
                }
            }

            return string.Join('/', path);
        }

        private static HashSet<string> CredentialParameterNames(RouteEndpoint endpoint) =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
                ? action.MethodInfo.GetParameters()
                    .Where(p => p.GetCustomAttribute<CredentialRouteValueAttribute>() is not null)
                    .Select(p => p.Name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
    }
}
