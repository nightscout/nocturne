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
    private static readonly string[] CredentialQueryParameters = ["token", "secret", "access_token"];

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

    private string? RedactRoute(string path)
    {
        var routes = _routes;
        if (routes.Length == 0 || path.Length == 0)
        {
            return null;
        }

        var pathOnly = path.AsSpan();
        var trimmed = pathOnly.Trim('/');

        foreach (var route in routes)
        {
            if (route.Matches(trimmed))
            {
                return string.Concat(
                    "/",
                    route.Redact(trimmed.ToString().Split('/')),
                    pathOnly.Length > 1 && pathOnly.EndsWith('/') ? "/" : ""
                );
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
