using Moq;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles;

internal static class ProfileScopedRepositoryMocks
{
    /// <summary>
    /// Answers the batch lookups of <see cref="IProfileScopedRepository{TRecord}"/> from
    /// <paramref name="stored"/>, read when each lookup runs, as the database would.
    /// </summary>
    public static void Serve<TRepository, TRecord>(this Mock<TRepository> repository, IList<TRecord> stored)
        where TRepository : class, IProfileScopedRepository<TRecord>
        where TRecord : class, IV4Record, IProfileScoped
    {
        repository
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) =>
                stored.Where(s => s.CorrelationId is { } id && ids.Contains(id)).ToList());
        repository
            .Setup(r => r.GetNewestByProfileNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> names, CancellationToken _) => stored
                .Where(s => names.Contains(s.ProfileName))
                .GroupBy(s => s.ProfileName)
                .Select(g => g.OrderByDescending(s => s.Timestamp).First())
                .ToList());
        repository
            .Setup(r => r.GetByProfileNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> names, CancellationToken _) =>
                stored.Where(s => names.Contains(s.ProfileName)).ToList());
        repository
            .Setup(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> ids, CancellationToken _) =>
                stored.Where(s => s.LegacyId is { } id && ids.Contains(id)).ToList());
    }

    /// <summary>
    /// Answers the batch document read of <see cref="ITherapySettingsRepository"/> from
    /// <paramref name="stored"/>, grouped by <see cref="TherapySettings.DocumentIdOf"/>.
    /// </summary>
    public static void ServeDocuments(this Mock<ITherapySettingsRepository> repository, IList<TherapySettings> stored) =>
        repository
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                (IReadOnlyDictionary<string, IReadOnlyList<TherapySettings>>)stored
                    .GroupBy(TherapySettings.DocumentIdOf)
                    .Where(g => ids.Contains(g.Key))
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<TherapySettings>)g.ToList()));
}
