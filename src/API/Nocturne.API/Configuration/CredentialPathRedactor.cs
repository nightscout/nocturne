using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing.Patterns;
using Nocturne.API.Attributes;

namespace Nocturne.API.Configuration;

/// <summary>
/// Replaces each <see cref="CredentialRouteValueAttribute"/> segment of a request path with its
/// route parameter name, e.g. <c>/api/v2/authorization/request/{accessToken}</c>.
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
        var routes = _routes;
        if (routes.Length == 0 || !path.StartsWith('/'))
        {
            return false;
        }

        var queryStart = path.IndexOf('?');
        var pathOnly = queryStart < 0 ? path.AsSpan() : path.AsSpan(0, queryStart);
        var trimmed = pathOnly.Trim('/');

        foreach (var route in routes)
        {
            if (route.Matches(trimmed))
            {
                redacted = string.Concat(
                    "/",
                    route.Redact(trimmed.ToString().Split('/')),
                    pathOnly.Length > 1 && pathOnly.EndsWith('/') ? "/" : "",
                    queryStart < 0 ? "" : path[queryStart..]
                );
                return true;
            }
        }

        return false;
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
