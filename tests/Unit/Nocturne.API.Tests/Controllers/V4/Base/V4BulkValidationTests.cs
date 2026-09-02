using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.V4;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// The validation every V4 bulk create-or-update endpoint runs before it maps or persists
/// anything: the guards themselves, and the wording each endpoint gives them.
/// </summary>
[Trait("Category", "Unit")]
public class V4BulkValidationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IApsSnapshotRepository> _aps = new();

    private ApsSnapshotController ApsController() => WithContext(new ApsSnapshotController(_aps.Object));

    private static TController WithContext<TController>(TController controller)
        where TController : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static void Rejected(ActionResult? result, string detail)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);

        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(400);
        problem.Title.Should().Be("Bad Request");
        problem.Detail.Should().Be(detail);
    }

    private void NothingPersisted() => _aps.Verify(
        r => r.BulkUpsertAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
        Times.Never);

    private static UpsertApsSnapshotRequest[] Snapshots(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new UpsertApsSnapshotRequest { Timestamp = T0.AddMinutes(i) })];

    // ── The guards, at a representative migrated endpoint ────────────

    [Fact]
    public async Task EmptyPayload_IsRejected()
    {
        Rejected((await ApsController().CreateApsSnapshots([])).Result, "APS snapshot data is required");
        NothingPersisted();
    }

    [Fact]
    public async Task NullPayload_IsRejected()
    {
        Rejected((await ApsController().CreateApsSnapshots(null!)).Result, "APS snapshot data is required");
        NothingPersisted();
    }

    [Fact]
    public async Task PayloadAtTheCap_IsAccepted()
    {
        _aps.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ApsSnapshot> models, WriteOrigin _, CancellationToken _) => [.. models]);

        var result = await ApsController().CreateApsSnapshots(Snapshots(1000));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task PayloadOverTheCap_IsRejected()
    {
        Rejected(
            (await ApsController().CreateApsSnapshots(Snapshots(1001))).Result,
            "Bulk operations are limited to 1000 snapshots per request");
        NothingPersisted();
    }

    [Fact]
    public async Task UnsetTimestampOnAnyItem_IsRejected()
    {
        var requests = Snapshots(3);
        requests[2].Timestamp = default;

        Rejected((await ApsController().CreateApsSnapshots(requests)).Result, "Timestamp must be set on every snapshot");
        NothingPersisted();
    }

    [Fact]
    public async Task SyncIdentifierWithoutDataSource_IsRejected()
    {
        var requests = Snapshots(2);
        requests[1].SyncIdentifier = "upstream-42";

        Rejected((await ApsController().CreateApsSnapshots(requests)).Result, "DataSource is required when SyncIdentifier is supplied");
        NothingPersisted();
    }

    [Fact]
    public async Task SyncIdentifierWithDataSource_IsAccepted()
    {
        _aps.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ApsSnapshot> models, WriteOrigin _, CancellationToken _) => [.. models]);

        var requests = Snapshots(1);
        requests[0].DataSource = "trio";
        requests[0].SyncIdentifier = "upstream-42";

        var result = await ApsController().CreateApsSnapshots(requests);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    // ── The wording each endpoint gives them ────────────────────────

    [Fact]
    public async Task EmptyPayload_NamesTheDataTheEndpointTakes()
    {
        Rejected((await ApsController().CreateApsSnapshots([])).Result, "APS snapshot data is required");
        Rejected((await PumpController().CreatePumpSnapshots([])).Result, "Pump snapshot data is required");
        Rejected((await UploaderController().CreateUploaderSnapshots([])).Result, "Uploader snapshot data is required");
        Rejected((await BasalInjections().CreateBasalInjectionsBulk([])).Result, "Basal injection data is required");
        Rejected((await Boluses().CreateBolusesBulk([])).Result, "Bolus data is required");
        Rejected((await CarbIntakes().CreateCarbIntakesBulk([])).Result, "Carb intake data is required");
        Rejected((await TempBasals().CreateTempBasals([])).Result, "Temp basal data is required");
        Rejected((await SensorGlucose().CreateSensorGlucoseBulk([])).Result, "Sensor glucose data is required");
    }

    [Fact]
    public async Task PayloadOverTheCap_NamesTheItemsTheEndpointTakes()
    {
        Rejected(
            (await PumpController().CreatePumpSnapshots(Fill(1001, () => new UpsertPumpSnapshotRequest()))).Result,
            "Bulk operations are limited to 1000 snapshots per request");
        Rejected(
            (await UploaderController().CreateUploaderSnapshots(Fill(1001, () => new UpsertUploaderSnapshotRequest()))).Result,
            "Bulk operations are limited to 1000 snapshots per request");
        Rejected(
            (await BasalInjections().CreateBasalInjectionsBulk(Fill(1001, () => new CreateBasalInjectionRequest { Timestamp = T0, Units = 1 }))).Result,
            "Bulk operations are limited to 1000 injections per request");
        Rejected(
            (await Boluses().CreateBolusesBulk(Fill(1001, () => new CreateBolusRequest()))).Result,
            "Bulk operations are limited to 1000 boluses per request");
        Rejected(
            (await CarbIntakes().CreateCarbIntakesBulk(Fill(1001, () => new CreateCarbIntakeRequest()))).Result,
            "Bulk operations are limited to 1000 intakes per request");
        Rejected(
            (await TempBasals().CreateTempBasals(Fill(1001, () => new CreateTempBasalRequest()))).Result,
            "Bulk operations are limited to 1000 temp basals per request");
        Rejected(
            (await SensorGlucose().CreateSensorGlucoseBulk(Fill(1001, () => new UpsertSensorGlucoseRequest()))).Result,
            "Bulk operations are limited to 1000 readings per request");
    }

    // ── Endpoints that did not run the full preamble before ─────────

    [Fact]
    public async Task SensorGlucoseBulk_RejectsAnUnsetTimestamp()
    {
        var repo = new Mock<ISensorGlucoseRepository>();

        var result = await SensorGlucose(repo).CreateSensorGlucoseBulk(
            [new UpsertSensorGlucoseRequest { Timestamp = T0, Mgdl = 120 }, new UpsertSensorGlucoseRequest { Mgdl = 115 }]);

        Rejected(result.Result, "Timestamp must be set on every reading");
        repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<Core.Models.V4.SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BasalInjectionBulk_RejectsAnUnsetTimestamp()
    {
        var repo = new Mock<IBasalInjectionRepository>();

        var result = await BasalInjections(repo).CreateBasalInjectionsBulk(
            [new CreateBasalInjectionRequest { Timestamp = T0, Units = 12 }, new CreateBasalInjectionRequest { Timestamp = default, Units = 12 }]);

        Rejected(result.Result, "Timestamp must be set on every injection");
        repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<BasalInjection>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── The per-item validators auto-validation cannot reach ────────

    [Fact]
    public async Task SensorGlucoseBulk_RejectsAReadingOutsideTheMgdlRange()
    {
        var repo = new Mock<ISensorGlucoseRepository>();

        var result = await WithValidators(SensorGlucose(repo)).CreateSensorGlucoseBulk(
            [new UpsertSensorGlucoseRequest { Timestamp = T0, Mgdl = -1 }]);

        Rejected(result.Result, "Sensor glucose at index 0 is invalid: Mgdl: Mgdl must be between 0 and 10000");
        repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<Core.Models.V4.SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SensorGlucoseBulk_NamesTheIndexOfTheOffendingReading()
    {
        var result = await WithValidators(SensorGlucose()).CreateSensorGlucoseBulk(
        [
            new UpsertSensorGlucoseRequest { Timestamp = T0, Mgdl = 120 },
            new UpsertSensorGlucoseRequest { Timestamp = T0.AddMinutes(5), Mgdl = 115 },
            new UpsertSensorGlucoseRequest { Timestamp = T0.AddMinutes(10), Mgdl = 1_000_000 },
        ]);

        Rejected(result.Result, "Sensor glucose at index 2 is invalid: Mgdl: Mgdl must be between 0 and 10000");
    }

    [Fact]
    public async Task SensorGlucoseBulk_AcceptsReadingsEveryRulePasses()
    {
        var repo = new Mock<ISensorGlucoseRepository>();
        repo.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<Core.Models.V4.SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Core.Models.V4.SensorGlucose> models, WriteOrigin _, CancellationToken _) => [.. models]);

        var result = await WithValidators(SensorGlucose(repo)).CreateSensorGlucoseBulk(
        [
            new UpsertSensorGlucoseRequest
            {
                Timestamp = T0,
                Mgdl = 120,
                Direction = GlucoseDirection.Flat,
                Device = "dexcom-g7",
                App = "xdrip",
                DataSource = "xdrip",
                GlucoseProcessing = "Smoothed",
            },
            new UpsertSensorGlucoseRequest { Timestamp = T0.AddMinutes(5), Mgdl = 0 },
        ]);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task ApsSnapshotBulk_HasNoValidator_SoNothingNewRejectsIt()
    {
        Validators.GetService<IValidator<UpsertApsSnapshotRequest>>().Should().BeNull();
        _aps.Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ApsSnapshot> models, WriteOrigin _, CancellationToken _) => [.. models]);

        var result = await WithValidators(ApsController()).CreateApsSnapshots(Snapshots(3));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    // ── Controllers ─────────────────────────────────────────────────

    /// <summary>
    /// The validators as <c>Program</c> registers them: scanned off the API assembly, so a request
    /// type nobody wrote a validator for resolves to nothing here too. MVC comes with them because
    /// a request-services-bearing controller resolves its <see cref="ProblemDetailsFactory"/> from
    /// there rather than falling back to the built-in one.
    /// </summary>
    private static readonly IServiceProvider Validators = BuildValidatorProvider();

    private static IServiceProvider BuildValidatorProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        services.AddValidatorsFromAssemblyContaining<UpsertSensorGlucoseRequestValidator>();
        return services.BuildServiceProvider();
    }

    private static TController WithValidators<TController>(TController controller)
        where TController : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = Validators },
        };
        return controller;
    }

    private static TRequest[] Fill<TRequest>(int count, Func<TRequest> item) =>
        [.. Enumerable.Range(0, count).Select(_ => item())];

    private static PumpSnapshotController PumpController() =>
        WithContext(new PumpSnapshotController(Mock.Of<IPumpSnapshotRepository>()));

    private static UploaderSnapshotController UploaderController() =>
        WithContext(new UploaderSnapshotController(Mock.Of<IUploaderSnapshotRepository>()));

    private static BasalInjectionController BasalInjections(Mock<IBasalInjectionRepository>? repo = null) =>
        WithContext(new BasalInjectionController(
            (repo ?? new Mock<IBasalInjectionRepository>()).Object, Mock.Of<IPatientInsulinRepository>()));

    private static BolusController Boluses() =>
        WithContext(new BolusController(
            Mock.Of<IBolusRepository>(),
            Mock.Of<IPatientInsulinRepository>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPatientDeviceStamper>()));

    // The database context is only reached past the preamble, which is all these tests exercise.
    private static NutritionController CarbIntakes() =>
        WithContext(new NutritionController(
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IDemoModeService>(),
            context: null!));

    private static TempBasalController TempBasals() =>
        WithContext(new TempBasalController(Mock.Of<ITempBasalRepository>(), Mock.Of<IPatientDeviceStamper>()));

    private static SensorGlucoseController SensorGlucose(Mock<ISensorGlucoseRepository>? repo = null) =>
        WithContext(new SensorGlucoseController(
            (repo ?? new Mock<ISensorGlucoseRepository>()).Object,
            Mock.Of<IGlucoseProcessingResolver>(),
            Mock.Of<ICanonicalAlertEvaluator>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<ILogger<SensorGlucoseController>>()));
}
