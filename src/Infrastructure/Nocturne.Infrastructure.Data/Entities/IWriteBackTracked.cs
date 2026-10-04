namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A legacy-keyed row that records whether a live write (<c>WriteOrigin.Live</c>) has touched it.
/// Nightscout write-back fires only on live writes, so a row no live write has touched, such as one
/// only a Nightscout migration imported, was never sent upstream, and a copy of it pulled back is
/// the upstream's own record rather than the row's write-back echo. Storage only, like
/// <see cref="IUpstreamFingerprinted"/>; set on save under <see cref="LiveWriteScope"/>, never
/// cleared.
/// </summary>
public interface IWriteBackTracked : IV4Entity, ISourcedEntity
{
    bool WrittenLive { get; set; }
}
