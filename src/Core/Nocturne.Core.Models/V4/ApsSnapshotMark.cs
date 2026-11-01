namespace Nocturne.Core.Models.V4;

/// <summary>Where and when an <see cref="ApsSnapshot"/> was taken, without its contents.</summary>
public sealed record ApsSnapshotMark(Guid Id, string? LegacyId, string? Device, DateTime Timestamp);
