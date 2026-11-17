using Moq;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// A loose mock of a legacy-keyed repository that holds nothing. The decomposers look up which
/// legacy ids are held before they point a pulled copy at its record, and a bare mock answers that
/// set with null.
/// </summary>
internal static class EmptyLegacyKeyed
{
    public static TRepo Of<TRepo, TRecord>()
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
        => new Mock<TRepo>().HoldingNothing<TRepo, TRecord>().Object;

    public static Mock<TRepo> HoldingNothing<TRepo, TRecord>(this Mock<TRepo> repository)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        repository
            .Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        return repository;
    }
}
