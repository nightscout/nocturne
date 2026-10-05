using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
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
    private readonly Mock<IDecompositionPipeline> _pipeline = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceDeleteByIdTests()
    {
        _service = new TreatmentReadService(
            _projection.Object,
            new Mock<ITreatmentDecomposer>().Object,
            _pipeline.Object,
            _tempBasalRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _bolusCalcRepo.Object,
            _stateSpans.Object,
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

        var deleted = await _service.DeleteAsync(clientId);

        deleted.Should().NotBeNull();
        deleted!.Served!.Id.Should().Be(spanId);
        _stateSpans.Verify(s => s.DeleteStateSpanAsync(spanId, It.IsAny<CancellationToken>()), Times.Once);
        _pipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Treatment>(clientId, WriteOrigin.Live, It.IsAny<CancellationToken>()),
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
        _pipeline
            .Setup(p => p.DeleteByLegacyIdAsync<Treatment>(
                bolus.LegacyId, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var deleted = await _service.DeleteAsync(id.ToString());

        deleted.Should().NotBeNull();
        _pipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Treatment>(bolus.LegacyId, WriteOrigin.Live, It.IsAny<CancellationToken>()),
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

        var deleted = await _service.DeleteAsync(id.ToString());

        deleted.Should().NotBeNull();
        _noteRepo.As<IV4Repository<Note>>().Verify(
            r => r.DeleteAsync(id, WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_RawGuidOwnedByNoRepo_DeletesNothing()
    {
        var deleted = await _service.DeleteAsync(Guid.NewGuid().ToString());

        deleted.Should().BeNull();
    }
}
