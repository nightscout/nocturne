namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// An entity that records where its row came from, through two independent handles:
/// <see cref="DataSource"/> — stamped by connector imports, the demo seeder and the V4 write
/// endpoints — and <see cref="Device"/>, the string the uploader reported for itself (a rig name
/// such as <c>openaps://host</c>, a phone model, or the connector's own id). Either may be null,
/// and a row's two handles frequently disagree.
///
/// Implementers MUST map both as ordinary EF columns (plain auto-properties with a <c>[Column]</c>
/// mapping) so generic <c>ctx.Set&lt;TEntity&gt;()</c> queries translate the interface-member access
/// to SQL, the same way they already do for <see cref="ITenantScoped.TenantId"/>.
/// </summary>
/// <seealso cref="Extensions.SourceFilter"/>
public interface ISourcedEntity
{
    /// <summary>Origin data source identifier.</summary>
    string? DataSource { get; set; }

    /// <summary>Device identifier the uploader reported.</summary>
    string? Device { get; set; }
}
