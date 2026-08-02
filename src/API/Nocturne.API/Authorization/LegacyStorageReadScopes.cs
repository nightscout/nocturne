using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Maps a legacy <c>storage</c> selector to the read scope that governs it.
/// </summary>
/// <remarks>
/// The V1 routes that take the collection as a parameter — <c>slice/{storage}/…</c>,
/// <c>times/echo?storage=</c>, <c>count/{storage}/where</c> — cannot be gated by an attribute, because a
/// class- or action-level scope list is an OR across every collection the route can serve and so
/// admits a caller holding any one of them to all of them. The storage is a route or query value, so
/// the governing scope is resolvable per request and is checked in the action instead.
/// </remarks>
/// <seealso cref="ActivityReadScopeGuard"/>
internal static class LegacyStorageReadScopes
{
    /// <summary>
    /// The read scope each storage selector requires, keyed case-insensitively because the services
    /// dispatch on <c>storage.ToLowerInvariant()</c>. <c>activity</c> is absent: it merges
    /// heart-rate, step-count, sleep and plain activity, so it has no single governing scope and is
    /// handled by <see cref="ActivityReadScopeGuard"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ScopesByStorage =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["entries"] = OAuthScopes.GlucoseRead,
            ["treatments"] = OAuthScopes.TreatmentsRead,
            ["devicestatus"] = OAuthScopes.DevicesRead,
            ["profile"] = OAuthScopes.TherapyRead,
            ["food"] = OAuthScopes.FoodRead,
        };

    /// <summary>
    /// The read scope <paramref name="storage"/> requires, or <see langword="null"/> when the
    /// selector names no known collection. A null means the caller supplied something the service
    /// will reject anyway, so the caller reports it as a bad request rather than a refusal.
    /// </summary>
    public static string? RequiredReadScope(string? storage) =>
        storage is not null && ScopesByStorage.TryGetValue(storage, out var scope) ? scope : null;

    /// <summary>
    /// Whether <paramref name="grantedScopes"/> may read <paramref name="storage"/>. False for an
    /// unknown selector, so a new collection is denied until it is classified here.
    /// </summary>
    public static bool CanRead(IReadOnlySet<string> grantedScopes, string? storage) =>
        RequiredReadScope(storage) is { } scope && OAuthScopes.SatisfiesScope(grantedScopes, scope);

    /// <summary>
    /// The result to return instead of reading <paramref name="storage"/>, or <see langword="null"/>
    /// when the caller may read it. An unclassified selector is refused rather than passed through,
    /// so adding a collection to a route's accepted list without classifying it here closes the
    /// route rather than opening it.
    /// </summary>
    public static ActionResult? RefuseRead(HttpContext httpContext, string? storage)
    {
        if (RequiredReadScope(storage) is not { } required)
        {
            return new BadRequestObjectResult(
                new { status = 400, message = $"Unsupported storage type: {storage}", type = "bad_request" });
        }

        if (CanRead(httpContext.GetGrantedScopes(), storage))
            return null;

        return new ObjectResult(
            new { status = 403, message = $"Reading '{storage}' requires the {required} scope.", type = "forbidden" })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
