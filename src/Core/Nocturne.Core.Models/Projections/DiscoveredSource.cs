namespace Nocturne.Core.Models.Projections;

/// <summary>
/// A distinct <c>(DataSource, Device)</c> combination seen in recent unattributed sensor glucose
/// readings — a candidate the user can register as a <c>PatientDevice</c>. Mirrors the pseudo-device
/// identity of <c>CanonicalGlucoseStream.StreamKey</c> so the settings UI and the canonical stream
/// name the same untracked streams.
/// </summary>
/// <param name="DataSource">The reading's data source (connector name, uploader app, etc.).</param>
/// <param name="Device">The reading's device string, or <c>null</c> when the source supplies none.</param>
/// <param name="ReadingCount">Number of unattributed readings from this combination in the queried window.</param>
/// <param name="LastSeen">Timestamp of the most recent unattributed reading from this combination.</param>
public sealed record DiscoveredSource(string? DataSource, string? Device, int ReadingCount, DateTime LastSeen);
