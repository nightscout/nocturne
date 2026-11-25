using System.Collections.Frozen;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>The context facts committed writes moved (docs/alerts/engine-semantics.md §5.2), by wire name.</summary>
/// <param name="Facts">The facts moved.</param>
/// <param name="GlucoseWithoutReading">
/// The glucose fact moved by a write that is not a reading, as a target-range schedule moves the
/// glucose bucket. The reading watermark must not drop such a move.
/// </param>
public sealed record MovedFacts(IReadOnlySet<string> Facts, bool GlucoseWithoutReading = false)
{
    /// <summary>
    /// The rules a committed save created, enabled or changed the conditions of. Each is evaluated
    /// whatever facts it reads, and past the reading watermark, since no reading has judged it yet.
    /// </summary>
    public IReadOnlySet<Guid> Rules { get; init; } = FrozenSet<Guid>.Empty;

    public bool IsEmpty => Facts.Count == 0 && Rules.Count == 0;

    /// <summary>The announcement of a save of <paramref name="rules"/>.</summary>
    public static MovedFacts Saved(IEnumerable<Guid> rules) =>
        new(FrozenSet<string>.Empty) { Rules = rules.ToFrozenSet() };
}
