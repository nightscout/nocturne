namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row decomposed from a legacy treatment, carrying the fingerprint of the upstream document a
/// connector last wrote it from. Storage only: no domain model or DTO maps it, and implementers mark
/// it <see cref="AuditIgnoredAttribute"/> so no audit record holds it either. Written through
/// <see cref="UpstreamFingerprintScope"/>.
/// </summary>
public interface IUpstreamFingerprinted : IV4Entity, ISourcedEntity
{
    string? UpstreamFingerprint { get; set; }
}
