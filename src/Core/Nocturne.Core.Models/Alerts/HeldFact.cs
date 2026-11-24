namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// A context fact a held rule is waiting for and why it is unknown, as the engine names them
/// (docs/alerts/engine-semantics.md §2.4): <see cref="Fact"/> is a fact's wire name
/// (<c>glucose</c>, <c>insulin</c>, ...) and <see cref="Reason"/> one of <c>absent</c>,
/// <c>stale</c>, <c>ahead_of_clock</c>, <c>unrecognised</c> or <c>inexact</c>. Both stay strings, so
/// a value a newer engine adds reaches a reader as it is.
/// </summary>
public sealed record HeldFact(string Fact, string Reason);

/// <summary>
/// A rule that is waiting for missing data (docs/alerts/engine-semantics.md §6.4): since when, and
/// the facts it waits for, as the engine named them on the last evaluation that changed them.
/// </summary>
public sealed record AlertHold(DateTime Since, IReadOnlyList<HeldFact> Facts)
{
    public static AlertHold Of(HeldRuleState state) => new(state.HeldSince, HeldFacts.Parse(state.HeldFacts));
}

/// <summary>The stored form of a hold's facts, <c>fact:reason</c> pairs joined by commas.</summary>
public static class HeldFacts
{
    public static string? Format(IEnumerable<HeldFact>? facts)
    {
        var pairs = facts?.Select(f => $"{f.Fact}:{f.Reason}").ToList();
        return pairs is { Count: > 0 } ? string.Join(',', pairs) : null;
    }

    public static IReadOnlyList<HeldFact> Parse(string? stored) =>
        string.IsNullOrEmpty(stored)
            ? []
            : stored.Split(',')
                .Select(pair => pair.Split(':', 2))
                .Select(parts => new HeldFact(parts[0], parts.Length > 1 ? parts[1] : ""))
                .ToList();
}
