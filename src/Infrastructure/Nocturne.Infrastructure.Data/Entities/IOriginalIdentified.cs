namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row addressable both by its own primary key and by the MongoDB ObjectId it carried into the
/// migration, so an id minted against the legacy database still resolves. The V4 clinical entities
/// spell the same idea as <see cref="IV4Entity.LegacyId"/>.
/// </summary>
public interface IOriginalIdentified : IIdentified
{
    /// <summary>The MongoDB ObjectId this row carried before migration, when it had one.</summary>
    string? OriginalId { get; set; }
}
