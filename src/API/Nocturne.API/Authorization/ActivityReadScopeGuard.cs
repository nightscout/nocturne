using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Enforces per-record OAuth read scopes on the merged activity read endpoints, the mirror of
/// <see cref="ActivityWriteScopeGuard"/>. <c>IActivityService.GetActivitiesAsync</c> merges four
/// storages into one response — StateSpans, heart rates, step counts and sleep sessions — and each
/// dedicated storage has its own read scope, so a single <c>RequireScope</c> attribute on the
/// action can only decide admission, not what the response may contain. The attribute therefore
/// lists <see cref="AdmissionScopes"/> as an OR and this guard drops every record whose category
/// the caller does not hold.
/// </summary>
/// <seealso cref="IActivityDecomposer.RequiredReadScope"/>
internal static class ActivityReadScopeGuard
{
    /// <summary>
    /// The read scopes that admit a caller to the merged activity read endpoints: holding any one
    /// of them means at least one category in the response is visible. Attribute arguments must be
    /// compile-time constants, so the admission attributes repeat these constants inline; the
    /// count endpoints, which cannot filter and so require all four, read them from here.
    /// </summary>
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
        Scope.TreatmentsRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead,
    ];

    /// <summary>
    /// The categories the caller may read, for a count that asks each source separately instead of
    /// filtering records the way <see cref="Filter"/> does. Named by the same read scope
    /// <see cref="IActivityDecomposer.RequiredReadScope"/> returns, so the two agree on what a
    /// category is.
    /// </summary>
    public static IReadOnlySet<string> GrantedCategories(HttpContext httpContext)
    {
        var granted = httpContext.GetGrantedScopes();
        return AdmissionScopes
            .Where(scope => Scope.Satisfies(granted, scope))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The result to return instead of a merged activity count, or <see langword="null"/> when the
    /// caller may have it. A single number cannot be attributed to a category the way
    /// <see cref="GrantedCategories"/> lets a per-category count be, so it takes every category
    /// rather than any one of them.
    /// </summary>
    public static ActionResult? RefuseUnlessEveryCategory(HttpContext httpContext)
    {
        var granted = httpContext.GetGrantedScopes();
        if (AdmissionScopes.All(scope => Scope.Satisfies(granted, scope)))
            return null;

        return new ObjectResult(new
        {
            status = 403,
            message = "Counting merged activity requires every activity category's read scope.",
            type = "forbidden",
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    /// <summary>
    /// Returns whether the caller's granted scopes cover the storage the activity came from.
    /// </summary>
    /// <param name="activity">The activity about to be returned.</param>
    /// <param name="decomposer">Classifier that maps each activity to its required read scope.</param>
    /// <param name="grantedScopes">The caller's normalized granted scopes.</param>
    public static bool CanRead(
        Activity activity,
        IActivityDecomposer decomposer,
        IReadOnlySet<string> grantedScopes)
    {
        return Scope.Satisfies(grantedScopes, decomposer.RequiredReadScope(activity));
    }

    /// <summary>
    /// Drops every activity whose storage category the caller lacks the read scope for.
    /// </summary>
    /// <param name="activities">The activities about to be returned.</param>
    /// <param name="decomposer">Classifier that maps each activity to its required read scope.</param>
    /// <param name="grantedScopes">The caller's normalized granted scopes.</param>
    public static List<Activity> Filter(
        IEnumerable<Activity> activities,
        IActivityDecomposer decomposer,
        IReadOnlySet<string> grantedScopes)
    {
        return activities
            .Where(activity => CanRead(activity, decomposer, grantedScopes))
            .ToList();
    }
}
