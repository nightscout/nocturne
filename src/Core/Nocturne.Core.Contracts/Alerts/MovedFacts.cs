namespace Nocturne.Core.Contracts.Alerts;

/// <summary>The context facts committed writes moved (docs/alerts/engine-semantics.md §5.2), by wire name.</summary>
/// <param name="Facts">The facts moved.</param>
/// <param name="GlucoseWithoutReading">
/// The glucose fact moved by a write that is not a reading, as a target-range schedule moves the
/// glucose bucket. The reading watermark must not drop such a move.
/// </param>
public sealed record MovedFacts(IReadOnlySet<string> Facts, bool GlucoseWithoutReading = false);
