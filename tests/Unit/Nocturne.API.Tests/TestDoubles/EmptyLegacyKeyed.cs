using Moq;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// A loose mock of a legacy-keyed repository that holds nothing. The decomposers look up which
/// legacy ids are held before they write, and a bare <c>Mock.Of</c> answers that set with null.
/// </summary>
internal static class EmptyLegacyKeyed
{
    public static TRepo Of<TRepo, TRecord>()
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        var repository = new Mock<TRepo>();
        repository
            .Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        return repository.Object;
    }
}
