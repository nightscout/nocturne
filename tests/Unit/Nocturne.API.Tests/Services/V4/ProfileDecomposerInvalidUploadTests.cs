using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A profile document sent with <c>isValid: false</c> is settled by the rule entries and treatments
/// follow, for every caller of the batch decomposition: stored deleted under an id no document holds,
/// deleting the live document its id names, and writing nothing under an id whose stores a deleted
/// document holds.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileDecomposerInvalidUploadTests
{
    private const string Id = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<ITherapySettingsRepository> _settings = Table<ITherapySettingsRepository, TherapySettings>();
    private readonly Mock<IBasalScheduleRepository> _basal = Table<IBasalScheduleRepository, BasalSchedule>();
    private readonly Mock<IProfileProjectionService> _projection = new();
    private readonly AuditContext _audit = new();

    public ProfileDecomposerInvalidUploadTests()
    {
        _settings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<TherapySettings>>());
        _settings
            .Setup(r => r.GetDeletedLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _settings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _settings.Setup(r => r.GetDefaultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private static Mock<TRepository> Table<TRepository, TRecord>()
        where TRepository : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        var mock = new Mock<TRepository>();
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.BulkUpsertByLegacyIdAsync(
                It.IsAny<IReadOnlyList<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TRecord> records, WriteOrigin _, bool _, CancellationToken _) =>
                new LegacyUpsertBatch<TRecord>(
                    records.ToDictionary(r => r.LegacyId!, r => new LegacyUpsert<TRecord>(r, UpsertOutcome.Created)), 0));
        return mock;
    }

    private ProfileDecomposer Decomposer() => new(
        _settings.Object,
        _basal.Object,
        Table<ICarbRatioScheduleRepository, CarbRatioSchedule>().Object,
        Table<ISensitivityScheduleRepository, SensitivitySchedule>().Object,
        Table<ITargetRangeScheduleRepository, TargetRangeSchedule>().Object,
        Mock.Of<ITempBasalRateReresolver>(),
        Mock.Of<ILoopClosedLoopStamper>(),
        _projection.Object,
        _audit,
        NullLogger<ProfileDecomposer>.Instance);

    private static Profile Document(string id = Id, bool? isValid = false) => new()
    {
        Id = id,
        IsValid = isValid,
        DefaultProfile = "Default",
        Store = new Dictionary<string, ProfileData> { ["Default"] = new() },
    };

    private void VerifyNothingUpserted() =>
        _settings.Verify(r => r.BulkUpsertByLegacyIdAsync(
            It.IsAny<IReadOnlyList<TherapySettings>>(), It.IsAny<WriteOrigin>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Fact]
    public async Task AnIdNoDocumentHolds_IsStoredDeleted_AndNotUpserted()
    {
        var upload = Document();

        var result = await Decomposer().DecomposeBatchAsync([upload], WriteOrigin.Backfill);

        _settings.Verify(r => r.StoreDeletedAsync(
            It.Is<IReadOnlyCollection<TherapySettings>>(s => s.Single().LegacyId == $"{Id}:Default"),
            It.IsAny<CancellationToken>()), Times.Once);
        _basal.Verify(r => r.StoreDeletedAsync(
            It.Is<IReadOnlyCollection<BasalSchedule>>(s => s.Single().LegacyId == $"{Id}:Default"),
            It.IsAny<CancellationToken>()), Times.Once);
        VerifyNothingUpserted();
        result.Invalidated.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WriteOrigin.Live)]
    [InlineData(WriteOrigin.Backfill)]
    public async Task AnIdALiveDocumentHolds_DeletesIt_AsTheSystem(WriteOrigin origin)
    {
        _settings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<TherapySettings>>
            {
                [Id] = [new TherapySettings { LegacyId = $"{Id}:Default" }],
            });
        bool? deletedAsSystem = null;
        _settings
            .Setup(r => r.DeleteByLegacyIdPrefixAsync($"{Id}:", origin, It.IsAny<CancellationToken>()))
            .Callback(() => deletedAsSystem = _audit.IsSystem)
            .ReturnsAsync(1);
        var upload = Document();

        var result = await Decomposer().DecomposeBatchAsync([upload], origin);

        result.Invalidated.Should().ContainSingle().Which.Should().BeSameAs(upload);
        deletedAsSystem.Should().BeTrue();
        _settings.Verify(r => r.StoreDeletedAsync(
            It.IsAny<IReadOnlyCollection<TherapySettings>>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNothingUpserted();
    }

    [Fact]
    public async Task AnIdTheServedIdentifierResolves_DeletesTheDocumentItNames()
    {
        const string served = "5f8d0c1e8a7b4c3d9e5f00ff";
        _projection.Setup(p => p.ResolveDocumentIdAsync(served, It.IsAny<CancellationToken>())).ReturnsAsync(Id);
        _settings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<TherapySettings>>
            {
                [Id] = [new TherapySettings { LegacyId = $"{Id}:Default" }],
            });
        _settings
            .Setup(r => r.DeleteByLegacyIdPrefixAsync($"{Id}:", WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await Decomposer().DecomposeBatchAsync([Document(served)], WriteOrigin.Live);

        result.Invalidated.Should().ContainSingle();
    }

    [Fact]
    public async Task AnIdWhoseStoresADeletedDocumentHolds_WritesNothing()
    {
        _settings
            .Setup(r => r.GetDeletedLegacyIdsAsync(
                It.Is<IReadOnlyCollection<string>>(ids => ids.Contains($"{Id}:Default")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { $"{Id}:Default" });

        var result = await Decomposer().DecomposeBatchAsync([Document()], WriteOrigin.Backfill);

        _settings.Verify(r => r.StoreDeletedAsync(
            It.IsAny<IReadOnlyCollection<TherapySettings>>(), It.IsAny<CancellationToken>()), Times.Never);
        _settings.Verify(r => r.DeleteByLegacyIdPrefixAsync(
            It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNothingUpserted();
        result.Invalidated.Should().BeEmpty();
    }

    [Fact]
    public async Task TheValidDocumentsOfTheBatchAreUpserted()
    {
        const string otherId = "5f8d0c1e8a7b4c3d9e5f0002";

        await Decomposer().DecomposeBatchAsync([Document(otherId, isValid: null), Document()], WriteOrigin.Live);

        _settings.Verify(r => r.BulkUpsertByLegacyIdAsync(
            It.Is<IReadOnlyList<TherapySettings>>(s => s.Single().LegacyId == $"{otherId}:Default"),
            WriteOrigin.Live, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
