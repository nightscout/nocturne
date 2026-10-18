using Moq;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Answers <see cref="ILegacyKeyedRepository{TRecord}.CreateOrUpsertAsync"/> through the mock's own
/// <see cref="IV4Repository{T}.CreateAsync"/>, as an insert, the way a repository that does not
/// upsert on a sync key implements it. Setups, throws and verifications written against
/// <c>CreateAsync</c> then hold for the decomposers, which create through <c>CreateOrUpsertAsync</c>.
/// </summary>
internal static class CreateOrUpsertForwarding
{
    public static Mock<TRepo> ForwardCreateOrUpsertToCreate<TRepo, TRecord>(this Mock<TRepo> repository)
        where TRepo : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        repository
            .Setup(r => r.CreateOrUpsertAsync(It.IsAny<TRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Returns(async (TRecord model, WriteOrigin origin, CancellationToken ct) =>
                new LegacyUpsert<TRecord>(await repository.Object.CreateAsync(model, origin, ct), Created: true));
        return repository;
    }
}
