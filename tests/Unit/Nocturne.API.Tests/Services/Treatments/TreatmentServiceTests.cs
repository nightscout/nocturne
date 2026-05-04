using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Treatments;

[Parity("api.treatments.test.js")]
public class TreatmentServiceTests
{
    private readonly Mock<ITreatmentStore> _mockStore;
    private readonly Mock<ITreatmentDecomposer> _mockDecomposer;
    private readonly Mock<ITreatmentCache> _mockCache;
    private readonly Mock<IDataEventSink<Treatment>> _mockEvents;
    private readonly Mock<IPatientInsulinRepository> _mockInsulinRepo;
    private readonly Mock<ILogger<TreatmentService>> _mockLogger;
    private readonly TreatmentService _treatmentService;

    public TreatmentServiceTests()
    {
        _mockStore = new Mock<ITreatmentStore>();
        _mockDecomposer = new Mock<ITreatmentDecomposer>();
        _mockCache = new Mock<ITreatmentCache>();
        _mockEvents = new Mock<IDataEventSink<Treatment>>();
        _mockInsulinRepo = new Mock<IPatientInsulinRepository>();
        _mockLogger = new Mock<ILogger<TreatmentService>>();
        _treatmentService = new TreatmentService(
            _mockStore.Object, _mockDecomposer.Object, _mockCache.Object, _mockEvents.Object,
            _mockInsulinRepo.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task GetTreatmentsAsync_ShouldQueryStoreViaCache()
    {
        var expected = new List<Treatment> { new Treatment { Id = "1", EventType = "Meal Bolus" } };
        _mockCache.Setup(x => x.GetOrComputeAsync(It.IsAny<TreatmentQuery>(), It.IsAny<Func<Task<IReadOnlyList<Treatment>>>>(), It.IsAny<CancellationToken>()))
            .Returns<TreatmentQuery, Func<Task<IReadOnlyList<Treatment>>>, CancellationToken>(async (q, c, ct) => await c());
        _mockStore.Setup(x => x.QueryAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected.AsReadOnly());
        var result = await _treatmentService.GetTreatmentsAsync(count: 10, skip: 0, cancellationToken: CancellationToken.None);
        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTreatmentsByRangeAsync_DelegatesToStoreWithBounds()
    {
        var expected = new List<Treatment>
        {
            new() { Id = "1", Mills = 1_700_000_500_000L, EventType = "Meal Bolus" },
            new() { Id = "2", Mills = 1_700_000_100_000L, EventType = "Carb Correction" },
        };
        _mockStore.Setup(x => x.GetByRangeAsync(1_700_000_000_000L, 1_700_000_999_000L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected.AsReadOnly());

        var result = await _treatmentService.GetTreatmentsByRangeAsync(
            1_700_000_000_000L, 1_700_000_999_000L, CancellationToken.None);

        result.Should().HaveCount(2);
        _mockStore.Verify(x => x.GetByRangeAsync(1_700_000_000_000L, 1_700_000_999_000L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTreatmentsByRangeAsync_EmptyRange_ReturnsEmpty()
    {
        _mockStore.Setup(x => x.GetByRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        var result = await _treatmentService.GetTreatmentsByRangeAsync(0L, 0L, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTreatmentsAsync_ShouldInvalidateCacheAndPublishEvents()
    {
        var created = new List<Treatment> { new Treatment { Id = "1" } };
        _mockStore.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>())).ReturnsAsync(created.AsReadOnly());
        var result = await _treatmentService.CreateTreatmentsAsync(new List<Treatment> { new Treatment() }, CancellationToken.None);
        result.Should().ContainSingle();
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockEvents.Verify(x => x.OnCreatedAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateTreatmentAsync_ShouldInvalidateCacheAndPublishEvent()
    {
        var updated = new Treatment { Id = "id", EventType = "Meal Bolus" };
        _mockStore.Setup(x => x.UpdateAsync("id", It.IsAny<Treatment>(), It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        var result = await _treatmentService.UpdateTreatmentAsync("id", new Treatment(), CancellationToken.None);
        result.Should().NotBeNull();
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockEvents.Verify(x => x.OnUpdatedAsync(updated, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateTreatmentAsync_WhenNotFound_ShouldNotInvalidateOrPublish()
    {
        _mockStore.Setup(x => x.UpdateAsync("x", It.IsAny<Treatment>(), It.IsAny<CancellationToken>())).ReturnsAsync((Treatment?)null);
        var result = await _treatmentService.UpdateTreatmentAsync("x", new Treatment(), CancellationToken.None);
        result.Should().BeNull();
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTreatmentAsync_ShouldInvalidateCacheAndPublishEvent()
    {
        var existing = new Treatment { Id = "id" };
        _mockStore.Setup(x => x.GetByIdAsync("id", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _mockStore.Setup(x => x.DeleteAsync("id", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result = await _treatmentService.DeleteTreatmentAsync("id", CancellationToken.None);
        result.Should().BeTrue();
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockEvents.Verify(x => x.OnDeletedAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTreatmentAsync_WhenNotFound_ShouldNotInvalidateOrPublish()
    {
        _mockStore.Setup(x => x.GetByIdAsync("x", It.IsAny<CancellationToken>())).ReturnsAsync((Treatment?)null);
        _mockStore.Setup(x => x.DeleteAsync("x", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var result = await _treatmentService.DeleteTreatmentAsync("x", CancellationToken.None);
        result.Should().BeFalse();
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_ShouldInvalidateCacheWhenDeleted()
    {
        _mockDecomposer.Setup(x => x.BulkDeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(5);
        var result = await _treatmentService.DeleteTreatmentsAsync("q", CancellationToken.None);
        result.Should().Be(5);
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_WhenNoneDeleted_ShouldNotInvalidateCache()
    {
        _mockDecomposer.Setup(x => x.BulkDeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var result = await _treatmentService.DeleteTreatmentsAsync("q", CancellationToken.None);
        result.Should().Be(0);
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PatchTreatmentAsync_WhenExists_AppliesPatchAndDecomposes()
    {
        var existing = new Treatment { Id = "t1", Mills = 1000, EventType = "Note", Notes = "old" };
        _mockStore.Setup(x => x.GetByIdAsync("t1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _mockDecomposer.Setup(x => x.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var patchJson = JsonSerializer.Deserialize<JsonElement>("{\"notes\":\"updated\"}");
        var result = await _treatmentService.PatchTreatmentAsync("t1", patchJson, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Notes.Should().Be("updated");
        _mockDecomposer.Verify(x => x.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCache.Verify(x => x.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockEvents.Verify(x => x.OnUpdatedAsync(It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PatchTreatmentAsync_WhenNotFound_ReturnsNull()
    {
        _mockStore.Setup(x => x.GetByIdAsync("x", It.IsAny<CancellationToken>())).ReturnsAsync((Treatment?)null);

        var patchJson = JsonSerializer.Deserialize<JsonElement>("{\"notes\":\"updated\"}");
        var result = await _treatmentService.PatchTreatmentAsync("x", patchJson, CancellationToken.None);

        result.Should().BeNull();
        _mockDecomposer.Verify(x => x.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #region Insulin Context Auto-Population

    [Fact]
    public async Task CreateTreatment_BolusTreatment_ShouldPopulateInsulinContext()
    {
        // Arrange
        var insulinId = Guid.NewGuid();
        var bolusInsulin = new PatientInsulin
        {
            Id = insulinId,
            Name = "Fiasp",
            Dia = 3.5,
            Peak = 55,
            Curve = "ultra-rapid",
            Concentration = 100,
            Role = InsulinRole.Bolus,
            IsPrimary = true,
            IsCurrent = true
        };

        _mockInsulinRepo.Setup(x => x.GetPrimaryBolusInsulinAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(bolusInsulin);

        var treatment = new Treatment { EventType = "Meal Bolus", Insulin = 5.0 };

        _mockStore.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<Treatment>, CancellationToken>((t, _) => Task.FromResult(t));

        // Act
        var result = (await _treatmentService.CreateTreatmentsAsync(new[] { treatment }, CancellationToken.None)).ToList();

        // Assert
        result.Should().ContainSingle();
        var created = result.First();
        created.InsulinContext.Should().NotBeNull();
        created.InsulinContext!.PatientInsulinId.Should().Be(insulinId);
        created.InsulinContext.InsulinName.Should().Be("Fiasp");
        created.InsulinContext.Dia.Should().Be(3.5);
        created.InsulinContext.Peak.Should().Be(55);
        created.InsulinContext.Curve.Should().Be("ultra-rapid");
        created.InsulinContext.Concentration.Should().Be(100);
    }

    [Fact]
    public async Task CreateTreatment_NonInsulinTreatment_ShouldNotPopulateContext()
    {
        // Arrange
        var treatment = new Treatment { EventType = "Note", Notes = "Feeling good" };

        _mockStore.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<Treatment>, CancellationToken>((t, _) => Task.FromResult(t));

        // Act
        var result = (await _treatmentService.CreateTreatmentsAsync(new[] { treatment }, CancellationToken.None)).ToList();

        // Assert
        result.Should().ContainSingle();
        result.First().InsulinContext.Should().BeNull();
        _mockInsulinRepo.Verify(x => x.GetPrimaryBolusInsulinAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockInsulinRepo.Verify(x => x.GetPrimaryBasalInsulinAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTreatment_WithExplicitContext_ShouldNotOverride()
    {
        // Arrange
        var existingContext = new TreatmentInsulinContext
        {
            PatientInsulinId = Guid.NewGuid(),
            InsulinName = "Humalog",
            Dia = 4.0,
            Peak = 75,
            Curve = "rapid-acting",
            Concentration = 100
        };

        var treatment = new Treatment
        {
            EventType = "Meal Bolus",
            Insulin = 5.0,
            InsulinContext = existingContext
        };

        _mockStore.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<Treatment>, CancellationToken>((t, _) => Task.FromResult(t));

        // Act
        var result = (await _treatmentService.CreateTreatmentsAsync(new[] { treatment }, CancellationToken.None)).ToList();

        // Assert
        result.Should().ContainSingle();
        result.First().InsulinContext.Should().BeSameAs(existingContext);
        _mockInsulinRepo.Verify(x => x.GetPrimaryBolusInsulinAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTreatment_TempBasal_ShouldPopulateFromBasalInsulin()
    {
        // Arrange
        var insulinId = Guid.NewGuid();
        var basalInsulin = new PatientInsulin
        {
            Id = insulinId,
            Name = "Tresiba",
            Dia = 24.0,
            Peak = 600,
            Curve = "ultra-long",
            Concentration = 100,
            Role = InsulinRole.Basal,
            IsPrimary = true,
            IsCurrent = true
        };

        _mockInsulinRepo.Setup(x => x.GetPrimaryBasalInsulinAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(basalInsulin);

        var treatment = new Treatment { EventType = "Temp Basal", Rate = 0.8, Duration = 30 };

        _mockStore.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<Treatment>, CancellationToken>((t, _) => Task.FromResult(t));

        // Act
        var result = (await _treatmentService.CreateTreatmentsAsync(new[] { treatment }, CancellationToken.None)).ToList();

        // Assert
        result.Should().ContainSingle();
        var created = result.First();
        created.InsulinContext.Should().NotBeNull();
        created.InsulinContext!.PatientInsulinId.Should().Be(insulinId);
        created.InsulinContext.InsulinName.Should().Be("Tresiba");
        created.InsulinContext.Dia.Should().Be(24.0);
        created.InsulinContext.Peak.Should().Be(600);
        created.InsulinContext.Curve.Should().Be("ultra-long");
        created.InsulinContext.Concentration.Should().Be(100);
    }

    #endregion
}
