namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A treatment row that keeps the <see cref="Core.Models.WriteBackFingerprint"/> of each upstream
/// document of its record a pull may bring back as an echo, in the list
/// <see cref="Core.Models.WriteBackFingerprint.Stored"/> describes. Null until upstream first accepts
/// a send of the row's record, so a non-null value also says write-back has sent it upstream. Storage only, like
/// <see cref="IWriteBackTracked.WrittenLive"/>; written through
/// <see cref="Core.Contracts.Treatments.IWriteBackSentRecords"/>.
/// </summary>
public interface IWriteBackSent : IWriteBackTracked
{
    string? WriteBackSentFingerprint { get; set; }
}
