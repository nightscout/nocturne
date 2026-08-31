namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row addressable both by its own primary key and by the MongoDB ObjectId it carried into the
/// migration, so an id minted against the legacy database still resolves. The V4 clinical entities
/// spell the same idea as <see cref="IV4Entity.LegacyId"/>.
/// </summary>
/// <remarks>
/// Implementers MUST map every member as an ordinary EF column (a plain auto-property with a
/// <c>[Column]</c> mapping) — not an explicit-interface implementation, backing-field-only, or
/// <c>[NotMapped]</c> — so generic <c>Set&lt;TEntity&gt;()</c> queries translate the
/// interface-member access to SQL, the same way they already do for
/// <see cref="ITenantScoped.TenantId"/> and <see cref="ISoftDeletable.DeletedAt"/>.
/// </remarks>
public interface IOriginalIdentified
{
    /// <summary>Primary key.</summary>
    Guid Id { get; set; }

    /// <summary>The MongoDB ObjectId this row carried before migration, when it had one.</summary>
    string? OriginalId { get; set; }
}
