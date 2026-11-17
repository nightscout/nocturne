using Nocturne.API.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Tests for <see cref="TreatmentReadService.DeleteAsync"/> with raw record UUIDs — the id shape
/// projected treatments carry in memory (the 24-hex ObjectId only exists on the wire), and what
/// the field-filtered bulk-delete path passes back in.
/// </summary>
public class TreatmentReadServiceDeleteByIdTests
{
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<IPerTreatmentDecomposer> _decomposer = new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<IWriteBackSentRecords> _sentRecords = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceDeleteByIdTests()
    {
        _service = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            _tempBasalRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _bolusCalcRepo.Object,
            Mock.Of<IProfileActivationRepository>(),
            _stateSpans.Object,
            _sentRecords.Object,
            NullLogger<TreatmentReadService>.Instance);
    }

    [Fact]
    public async Task DeleteAsync_IdOfNoRecord_DeletesTheStateSpanItResolvesTo()
    {
        const string clientId = "8F2C1D4E-6B7A-4C3D-9E8F-1A2B3C4D5E6F";
        var spanId = Guid.CreateVersion7().ToString();
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { Id = spanId, EventType = "Temporary Override" });
        _projection
            .Setup(p => p.GetStateSpanTreatmentIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientId);
        _stateSpans.Setup(s => s.DeleteStateSpanAsync(spanId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var deleted = await _service.DeleteAsync(clientId, findUpstreamCopies: false);

        deleted.Should().NotBeNull();
        deleted!.Served!.Id.Should().Be(spanId);
        _stateSpans.Verify(s => s.DeleteStateSpanAsync(spanId, It.IsAny<CancellationToken>()), Times.Once);
        _decomposer.Verify(
            p => p.DeleteByLegacyIdAsync(clientId, WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_RawGuidWithLegacyId_DeletesViaLegacyIdForSiblingCleanup()
    {
        var id = Guid.NewGuid();
        var bolus = new Bolus
        {
            Id = id,
            LegacyId = "507f1f77bcf86cd799439011",
            Timestamp = DateTime.UtcNow,
        };
        // The delete helper resolves records through the IV4Repository<T> base slot (the derived
        // interfaces re-declare GetByIdAsync/DeleteAsync with `new`; concrete repos implement both
        // slots with one method, so only mocks need to distinguish them)
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync(
                bolus.LegacyId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var deleted = await _service.DeleteAsync(id.ToString(), findUpstreamCopies: false);

        deleted.Should().NotBeNull();
        _decomposer.Verify(
            p => p.DeleteByLegacyIdAsync(bolus.LegacyId, WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
        _bolusRepo.As<IV4Repository<Bolus>>().Verify(
            r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_RawGuidWithoutLegacyId_DeletesDirectlyFromOwningRepo()
    {
        var id = Guid.NewGuid();
        var note = new Note { Id = id, Timestamp = DateTime.UtcNow };
        _noteRepo.As<IV4Repository<Note>>()
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(note);

        var deleted = await _service.DeleteAsync(id.ToString(), findUpstreamCopies: false);

        deleted.Should().NotBeNull();
        _noteRepo.As<IV4Repository<Note>>().Verify(
            r => r.DeleteAsync(id, WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_RawGuidOwnedByNoRepo_DeletesNothing()
    {
        var deleted = await _service.DeleteAsync(Guid.NewGuid().ToString(), findUpstreamCopies: false);

        deleted.Should().BeNull();
    }

    private Bolus StoredBolus(string legacyId)
    {
        var bolus = new Bolus { Id = Guid.NewGuid(), LegacyId = legacyId, Timestamp = DateTime.UtcNow };
        _bolusRepo.As<IV4Repository<Bolus>>()
            .Setup(r => r.GetByIdAsync(bolus.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync(legacyId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return bolus;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteAsync_NamesTheRecordForUpstreamOnlyWhenItsOwnRowHoldsAnAcceptedSend(bool sent)
    {
        var bolus = StoredBolus("507f1f77bcf86cd799439011");
        _sentRecords
            .Setup(r => r.GetSentRowsAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { bolus.Id })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sent ? new HashSet<Guid> { bolus.Id } : new HashSet<Guid>());

        var deleted = await _service.DeleteAsync(bolus.Id.ToString(), findUpstreamCopies: true);

        deleted!.UpstreamLegacyIds.Should().Equal(sent ? [bolus.LegacyId!] : []);
        _sentRecords.Verify(r => r.GetSentUpstreamAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_ARecordTheNightscoutConnectorPulled_IsNeverNamedForUpstream()
    {
        var bolus = StoredBolus("507f1f77bcf86cd799439011");
        bolus.DataSource = DataSources.NightscoutConnector;
        _sentRecords
            .Setup(r => r.GetSentRowsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { bolus.Id });

        var deleted = await _service.DeleteAsync(bolus.Id.ToString(), findUpstreamCopies: true);

        deleted!.UpstreamLegacyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_NotAskedForUpstreamCopies_NeverReadsWhetherARecordWasSent()
    {
        var bolus = StoredBolus("507f1f77bcf86cd799439011");

        var deleted = await _service.DeleteAsync(bolus.Id.ToString(), findUpstreamCopies: false);

        deleted!.UpstreamLegacyIds.Should().BeEmpty();
        _sentRecords.VerifyNoOtherCalls();
    }
}
