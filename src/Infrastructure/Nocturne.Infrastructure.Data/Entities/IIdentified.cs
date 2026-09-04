namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row addressable by its own primary key.
/// </summary>
/// <remarks>
/// Implementers MUST map every member of this interface and its derivatives as an ordinary EF column
/// (a plain auto-property with a <c>[Column]</c> mapping) — not an explicit-interface
/// implementation, backing-field-only, or <c>[NotMapped]</c> — so generic queries over an
/// <c>IQueryable&lt;TEntity&gt;</c> constrained to the interface translate the interface-member
/// access to SQL, the same way they already do for <see cref="ITenantScoped.TenantId"/> and
/// <see cref="ISoftDeletable.DeletedAt"/>.
/// </remarks>
public interface IIdentified
{
    /// <summary>Primary key.</summary>
    Guid Id { get; set; }
}
