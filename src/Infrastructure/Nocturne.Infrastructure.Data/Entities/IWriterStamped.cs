namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A record that keeps the credential that wrote it. <see cref="ISourcedEntity"/> holds what the
/// uploader said about itself, which differs per record type for one app; the grant is the same for
/// every row one credential writes.
/// </summary>
/// <remarks>
/// Stamped on insert by <see cref="NocturneDbContext"/> from the scope's audit actor and never
/// changed afterwards, so a later edit by another credential leaves the row with its first writer.
/// Null for a row a connector, a background job or a signed-in member wrote, and for a row older
/// than the column.
/// </remarks>
public interface IWriterStamped
{
    /// <summary>The <c>oauth_grants</c> id of the credential that inserted the row.</summary>
    Guid? WriterGrantId { get; set; }
}
