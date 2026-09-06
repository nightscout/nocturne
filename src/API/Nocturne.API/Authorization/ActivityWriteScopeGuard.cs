using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Enforces per-record OAuth write scopes on the merged activity write endpoints (v1 and v4).
/// A single activity batch can fan out to heart-rate, step-count, sleep, and regular-activity
/// storage, and each dedicated destination has its own write scope. The dedicated controllers
/// (HeartRate, StepCount, Sleep) gate with <see cref="Attributes.RequireScopeAttribute"/>, but the
/// activity endpoints route by record content at the service layer, so the equivalent gate lives
/// here and is applied by the controllers before delegating.
/// </summary>
/// <remarks>
/// Internal connector publishing (<c>MetadataPublisher</c>) calls <c>IActivityService</c> directly,
/// bypassing the controllers, so it is intentionally not gated — a connector writes its own
/// heart-rate/step/sleep data without the triggering member's category scopes.
/// </remarks>
/// <seealso cref="IActivityDecomposer.RequiredWriteScope"/>
internal static class ActivityWriteScopeGuard
{
    /// <summary>
    /// Returns the first write scope the caller is missing for any record in the batch, or
    /// <see langword="null"/> when every record's destination scope is satisfied (including the
    /// regular activities that require no category scope).
    /// </summary>
    /// <param name="activities">The activities about to be written.</param>
    /// <param name="decomposer">Classifier that maps each activity to its required write scope.</param>
    /// <param name="grantedScopes">The caller's normalized granted scopes.</param>
    public static string? FindMissingScope(
        IEnumerable<Activity> activities,
        IActivityDecomposer decomposer,
        IReadOnlySet<string> grantedScopes)
    {
        foreach (var scope in activities
            .Select(decomposer.RequiredWriteScope)
            .Where(scope => scope is not null)
            .Distinct())
        {
            if (!Scope.Satisfies(grantedScopes, scope!))
                return scope;
        }

        return null;
    }
}
