using System.Reflection;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Enforces per-category OAuth read scopes on the fact timelines an alert replay returns, the
/// same shape as <see cref="ActogramReadScopeGuard"/>. The replay events and their leaf logs are
/// the alerts category, which the action's <c>RequireScope</c> admits on, but
/// <c>AlertReplayResult.FactTimelines</c> additionally carries every
/// <see cref="ReplayFactAttribute"/>-tagged property on <see cref="SensorContext"/> — glucose,
/// treatment and device data in one response — so admission alone cannot decide what the response
/// may contain. Each fact declares its category on the attribute and this guard drops the ones the
/// caller does not hold.
/// </summary>
internal static class AlertReplayReadScopeGuard
{
    private static readonly IReadOnlyDictionary<string, string> FactScopes = BuildFactScopes();

    /// <summary>
    /// Drops every fact timeline whose read scope the caller does not hold. A key with no declared
    /// category is dropped too, so an unclassified fact is hidden rather than shared.
    /// </summary>
    /// <param name="result">The replay result about to be returned.</param>
    /// <param name="grantedScopes">The caller's normalized granted scopes.</param>
    public static AlertReplayResult Redact(
        AlertReplayResult result,
        IReadOnlySet<string> grantedScopes)
    {
        var visible = result.FactTimelines
            .Where(fact => FactScopes.TryGetValue(fact.Key, out var scope)
                           && OAuthScopes.SatisfiesScope(grantedScopes, scope))
            .ToDictionary(fact => fact.Key, fact => fact.Value, StringComparer.Ordinal);

        return visible.Count == result.FactTimelines.Count
            ? result
            : result with { FactTimelines = visible };
    }

    private static Dictionary<string, string> BuildFactScopes() =>
        typeof(SensorContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<ReplayFactAttribute>(inherit: false))
            .Where(attribute => attribute is not null)
            .ToDictionary(attribute => attribute!.Key, attribute => attribute!.Scope, StringComparer.Ordinal);
}
