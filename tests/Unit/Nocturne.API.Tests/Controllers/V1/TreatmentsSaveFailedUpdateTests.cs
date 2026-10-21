using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// A save whose update of a stored treatment fails answers a server error. Only a treatment that
/// is not stored falls through to the create, so a failed edit cannot insert a second copy.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsSaveFailedUpdateTests
{
    private const string StoredId = "69F15FD2-8075-4DEB-AEA3-4352F455840D";

    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<ITreatmentDecomposer> _decomposer = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly TreatmentsController _controller;

    public TreatmentsSaveFailedUpdateTests()
    {
        var store = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            Mock.Of<IDecompositionPipeline>(),
            Mock.Of<ITempBasalRepository>(),
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        var service = new TreatmentService(
            store,
            _decomposer.Object,
            Mock.Of<ITreatmentCache>(),
            Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(),
            NullLogger<TreatmentService>.Instance);

        _controller = new TreatmentsController(
            service,
            new DocumentProcessingService(NullLogger<DocumentProcessingService>.Instance),
            TimeProvider.System,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());
    }

    [Fact]
    public async Task Put_WhenUpdatingAStoredRecordFails_DoesNotCreate()
    {
        StoreCarb();
        FailFirstDecomposition();

        var save = () => _controller.SaveTreatments(Body());

        await save.Should().ThrowAsync<DbUpdateException>();
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task Put_WhenUpdatingAStoredStateSpanFails_DoesNotCreate()
    {
        StoreSpan();
        FailFirstDecomposition();

        var save = () => _controller.SaveTreatments(Body());

        await save.Should().ThrowAsync<DbUpdateException>();
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task PutById_WhenUpdatingAStoredRecordFails_Answers500AndDoesNotCreate()
    {
        StoreCarb();
        FailFirstDecomposition();

        var result = await _controller.UpdateTreatment(StoredId, new Treatment { EventType = "Carb Correction", Carbs = 25 });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task PutById_WhenUpdatingAStoredStateSpanFails_Answers500AndDoesNotCreate()
    {
        StoreSpan();
        FailFirstDecomposition();

        var result = await _controller.UpdateTreatment(StoredId, new Treatment { EventType = "Temporary Override", Duration = 30 });

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task Put_WhenNothingIsStoredUnderTheId_CreatesIt()
    {
        var result = await _controller.SaveTreatments(Body());

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task Put_WhenTheUpdateDropsAMealHalf_UpdatesItAndDoesNotCreate()
    {
        var bolus = new Bolus { Id = Guid.CreateVersion7(), LegacyId = StoredId };
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = StoredId };
        _bolusRepo.Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), false, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = bolus.Id.ToString() }, new Treatment { Id = carb.Id.ToString() }]);
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => _bolusRepo
                .Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Bolus?)null))
            .ReturnsAsync(new DecompositionResult());

        var result = await _controller.SaveTreatments(Body());

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Treatment>().Which.Id.Should().Be(carb.Id.ToString());
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task Put_WhenAWrittenTreatmentReadsBackAsNothing_Fails()
    {
        var carb = StoreCarb();
        _projection
            .SetupSequence(p => p.GetProjectedTreatmentsAsync(It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), false, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = carb.Id.ToString() }])
            .ReturnsAsync([]);

        var save = () => _controller.SaveTreatments(Body());

        await save.Should().ThrowAsync<InvalidOperationException>();
        VerifyDecomposedOnce();
    }

    [Fact]
    public async Task Put_WhenAWrittenStateSpanReadsBackAsNothing_FailsAndDoesNotCreate()
    {
        _projection
            .SetupSequence(p => p.GetProjectedStateSpanTreatmentAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { Id = "span-1" });
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync("span-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment?)null);
        _projection
            .Setup(p => p.GetStateSpanTreatmentIdAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredId);

        var save = () => _controller.SaveTreatments(Body());

        await save.Should().ThrowAsync<InvalidOperationException>();
        VerifyDecomposedOnce();
    }

    private CarbIntake StoreCarb()
    {
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = StoredId };
        _carbIntakeRepo
            .Setup(r => r.GetByLegacyIdAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(carb);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), false, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = carb.Id.ToString() }]);
        return carb;
    }

    private void StoreSpan()
    {
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { Id = "span-1" });
        _projection
            .Setup(p => p.GetStateSpanTreatmentIdAsync(StoredId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredId);
    }

    private void FailFirstDecomposition() => _decomposer
        .SetupSequence(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new DbUpdateException("store down"))
        .ReturnsAsync(new DecompositionResult());

    private void VerifyDecomposedOnce() => _decomposer.Verify(d => d.DecomposeAsync(
        It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);

    private static JsonElement Body() => JsonSerializer.SerializeToElement(new
    {
        _id = StoredId,
        eventType = "Carb Correction",
        carbs = 25,
        created_at = "2026-09-30T01:45:00.000Z",
    });
}
