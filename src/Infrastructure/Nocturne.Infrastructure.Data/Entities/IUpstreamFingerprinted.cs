namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row a connector writes from an upstream treatment (a decomposed row, or the state span of an
/// override or temporary target), carrying the fingerprint of the document it last wrote the row from.
/// Storage only: no domain model or DTO maps it, and implementers mark it
/// <see cref="AuditIgnoredAttribute"/> so no audit record holds it either. Written through
/// <see cref="Services.UpstreamFingerprintScope"/>.
/// </summary>
public interface IUpstreamFingerprinted : ILegacyIdentified
{
    string? UpstreamFingerprint { get; set; }

    /// <summary>The source <see cref="Services.UpstreamFingerprintScope"/> names the row under, with its legacy id.</summary>
    string? UpstreamSource => this is ISourcedEntity sourced ? sourced.DataSource : null;
}
