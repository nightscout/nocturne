using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.V4;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
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
        r => r.BulkCreateAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
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
        _aps.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
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
        _aps.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ApsSnapshot> models, WriteOrigin _, CancellationToken _) => [.. models]);

        var requests = Snapshots(1);
        requests[0].DataSource = "trio";
        requests[0].SyncIdentifier = "upstream-42";

        var result = await ApsController().CreateApsSnapshots(requests);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    // ── The wording each endpoint gives them ────────────────────────

    /// <summary>
    /// Every controller reached through <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>,
    /// driven through its own <c>bulk</c> action, so a new one cannot ship with wording nobody read.
    /// The controllers below this are the bulk endpoints that do NOT come off that base and so are
    /// still named one at a time.
    /// </summary>
    [Theory]
    [MemberData(nameof(CrudControllers))]
    public async Task EveryCrudBulkEndpoint_NamesTheDataItTakes(Type controllerType)
    {
        var naming = BulkNamingOf(controllerType);

        naming.Subject.Should().MatchRegex("^[A-Z]", "the subject opens a sentence");
        naming.Singular.Should().Be(naming.Singular.ToLowerInvariant(), "the singular sits mid-sentence");
        naming.Plural.Should().Be(naming.Plural.ToLowerInvariant(), "the plural sits mid-sentence");
        naming.Plural.Should().NotBe(naming.Singular, "one item and many read differently");

        Rejected(await DriveBulkAsync(controllerType, 0), $"{naming.Subject} data is required");
        Rejected(
            await DriveBulkAsync(controllerType, V4BulkValidation.MaxItems + 1),
            $"Bulk operations are limited to {V4BulkValidation.MaxItems} {naming.Plural} per request");
        Rejected(await DriveBulkAsync(controllerType, 1), $"Timestamp must be set on every {naming.Singular}");
    }

    public static TheoryData<Type> CrudControllers()
    {
        var controllers = typeof(V4BulkValidation).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false } && DerivesFromCrudBase(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        controllers.Should().NotBeEmpty("the sweep proves nothing if it discovers no controllers");

        var data = new TheoryData<Type>();
        foreach (var type in controllers)
            data.Add(type);

        return data;
    }

    private static bool DerivesFromCrudBase(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(V4CrudControllerBase<,,,>))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads <c>BulkNaming</c> off an unconstructed controller and posts <paramref name="count"/>
    /// freshly-constructed requests to its <c>CreateBulk</c>. The getter returns a constant and the
    /// payload never clears validation, so no dependency is reached — several of these controllers
    /// take a <c>NocturneDbContext</c>, which has no mockable constructor.
    /// </summary>
    private static ControllerBase UnconstructedController(Type controllerType)
    {
        var controller = (ControllerBase)RuntimeHelpers.GetUninitializedObject(controllerType);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static V4BulkNaming BulkNamingOf(Type controllerType)
    {
        var property = controllerType.GetProperty("BulkNaming", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{controllerType.Name} declares no BulkNaming");

        return (V4BulkNaming)property.GetValue(UnconstructedController(controllerType))!;
    }

    private static async Task<ActionResult?> DriveBulkAsync(Type controllerType, int count)
    {
        var action = controllerType.GetMethod("CreateBulk")
            ?? throw new InvalidOperationException($"{controllerType.Name} exposes no CreateBulk");

        var requestType = action.GetParameters()[0].ParameterType.GetGenericArguments()[0];
        var payload = Array.CreateInstance(requestType, count);
        for (var i = 0; i < count; i++)
            payload.SetValue(Activator.CreateInstance(requestType), i);

        var task = (Task)action.Invoke(UnconstructedController(controllerType), [payload, CancellationToken.None])!;
        await task;

        var actionResultOfT = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (ActionResult?)actionResultOfT.GetType().GetProperty("Result")!.GetValue(actionResultOfT);
    }

    [Fact]
    public async Task EmptyPayload_NamesTheDataTheEndpointTakes()
    {
        Rejected((await ApsController().CreateApsSnapshots([])).Result, "APS snapshot data is required");
        Rejected((await PumpController().CreatePumpSnapshots([])).Result, "Pump snapshot data is required");
        Rejected((await UploaderController().CreateUploaderSnapshots([])).Result, "Uploader snapshot data is required");
        Rejected((await CarbIntakes().CreateCarbIntakesBulk([])).Result, "Carb intake data is required");
        Rejected((await TempBasals().CreateTempBasals([])).Result, "Temp basal data is required");
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
            (await CarbIntakes().CreateCarbIntakesBulk(Fill(1001, () => new CreateCarbIntakeRequest()))).Result,
            "Bulk operations are limited to 1000 intakes per request");
        Rejected(
            (await TempBasals().CreateTempBasals(Fill(1001, () => new CreateTempBasalRequest()))).Result,
            "Bulk operations are limited to 1000 temp basals per request");
    }

    [Fact]
    public async Task ActivityBulk_OverTheCap_IsRejected()
    {
        var service = new Mock<IActivityService>();

        var result = await Activities(service).CreateActivities(Fill(1001, () => new UpsertActivityRequest()));

        Rejected(result.Result, "Bulk operations are limited to 1000 activity records per request");
        service.Verify(
            s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ActivityBulk_NullPayload_IsRejected()
    {
        var service = new Mock<IActivityService>();

        var result = await Activities(service).CreateActivities(null!);

        Rejected(result.Result, "Activity data is required");
        service.Verify(
            s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HeartRateBulk_OverTheCap_IsRejected()
    {
        var service = new Mock<IHeartRateService>();

        var result = await HeartRates(service).CreateHeartRates(Fill(1001, () => new UpsertHeartRateRequest()));

        Rejected(result.Result, "Bulk operations are limited to 1000 heart rate records per request");
        service.Verify(
            s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HeartRateBulk_NullPayload_IsRejected()
    {
        var service = new Mock<IHeartRateService>();

        var result = await HeartRates(service).CreateHeartRates(null!);

        Rejected(result.Result, "Heart rate data is required");
        service.Verify(
            s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StepCountBulk_OverTheCap_IsRejected()
    {
        var service = new Mock<IStepCountService>();

        var result = await StepCounts(service).CreateStepCounts(Fill(1001, () => new UpsertStepCountRequest()));

        Rejected(result.Result, "Bulk operations are limited to 1000 step count records per request");
        service.Verify(
            s => s.CreateStepCountsAsync(It.IsAny<IEnumerable<StepCount>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StepCountBulk_NullPayload_IsRejected()
    {
        var service = new Mock<IStepCountService>();

        var result = await StepCounts(service).CreateStepCounts(null!);

        Rejected(result.Result, "Step count data is required");
        service.Verify(
            s => s.CreateStepCountsAsync(It.IsAny<IEnumerable<StepCount>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BodyWeightBatch_OverTheCap_IsRejected()
    {
        var service = new Mock<IBodyWeightService>();

        var result = await BodyWeights(service).CreateBodyWeights(Fill(1001, () => new BodyWeight()));

        Rejected(result.Result, "Bulk operations are limited to 1000 body weight records per request");
        service.Verify(
            s => s.CreateBodyWeightsAsync(It.IsAny<IEnumerable<BodyWeight>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BodyWeightBatch_EmptyPayload_IsRejected()
    {
        var service = new Mock<IBodyWeightService>();

        var result = await BodyWeights(service).CreateBodyWeights([]);

        Rejected(result.Result, "Body weight data is required");
        service.Verify(
            s => s.CreateBodyWeightsAsync(It.IsAny<IEnumerable<BodyWeight>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SleepBulk_OverItsOwnLowerCap_IsRejected()
    {
        var service = new Mock<ISleepService>();

        var result = await Sleep(service).CreateSessionsBulk(Fill(101, () => new SleepSession()));

        Rejected(result.Result, "Bulk operations are limited to 100 sessions per request");
        service.Verify(
            s => s.UpsertSessionAsync(It.IsAny<SleepSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── The per-item validators auto-validation cannot reach ────────

    [Fact]
    public async Task SensorGlucoseBulk_RejectsAReadingOutsideTheMgdlRange()
    {
        var repo = new Mock<ISensorGlucoseRepository>();

        var result = await WithValidators(SensorGlucose(repo)).CreateBulk(
            [new UpsertSensorGlucoseRequest { Timestamp = T0, Mgdl = -1 }]);

        Rejected(result.Result, "Sensor glucose at index 0 is invalid: Mgdl: Mgdl must be between 0 and 10000");
        repo.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<Core.Models.V4.SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SensorGlucoseBulk_NamesTheIndexOfTheOffendingReading()
    {
        var result = await WithValidators(SensorGlucose()).CreateBulk(
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

        var result = await WithValidators(SensorGlucose(repo)).CreateBulk(
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
        _aps.Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
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

    private static ActivityController Activities(Mock<IActivityService> service) =>
        WithContext(new ActivityController(service.Object, Mock.Of<IActivityDecomposer>()));

    private static HeartRateController HeartRates(Mock<IHeartRateService> service) =>
        WithContext(new HeartRateController(service.Object));

    private static StepCountController StepCounts(Mock<IStepCountService> service) =>
        WithContext(new StepCountController(service.Object));

    private static BodyWeightController BodyWeights(Mock<IBodyWeightService> service) =>
        WithContext(new BodyWeightController(service.Object));

    private static SleepController Sleep(Mock<ISleepService> service) =>
        WithContext(new SleepController(service.Object));

    private static SensorGlucoseController SensorGlucose(Mock<ISensorGlucoseRepository>? repo = null) =>
        WithContext(new SensorGlucoseController(
            (repo ?? new Mock<ISensorGlucoseRepository>()).Object,
            Mock.Of<IGlucoseProcessingResolver>(),
            Mock.Of<ICanonicalAlertEvaluator>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<ILogger<SensorGlucoseController>>()));
}
