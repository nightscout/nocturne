namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A tenant-scoped, soft-deletable row whose <see cref="LegacyId"/> is its re-import key: at most one
/// live row of a tenant holds a legacy id, which a partial unique index enforces.
/// </summary>
public interface ILegacyIdentified : ITenantScoped, ISoftDeletable, IIdentified
{
    /// <summary>The record's legacy (Nightscout/MongoDB) identifier, kept for v1-v3 round-tripping.</summary>
    string? LegacyId { get; set; }
}
