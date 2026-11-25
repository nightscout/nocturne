namespace Nocturne.Core.Models;

/// <summary>A stored treatment record's legacy id, with the event time it starts at.</summary>
/// <param name="Running">For a state span, whether it has no end yet.</param>
public sealed record StoredTreatmentKey(string LegacyId, DateTime At, bool Running = false);
