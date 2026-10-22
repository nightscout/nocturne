using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using LegacyV1 = Nocturne.API.Controllers.V1;
using LegacyV3 = Nocturne.API.Controllers.V3;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// A legacy POST reads its body the way the formatter binds a PUT: numbers may arrive as strings,
/// property names in any case, and one document or an array.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyPostBodyTests
{
    private const string Millis = "\"1700000000000\"";

    private sealed record Handler(
        string Single,
        string Malformed,
        Func<string, Task<IActionResult>> Post,
        Func<int> Count,
        Func<bool> Bound);

    public static TheoryData<string> Handlers =>
    [
        "v1 treatments",
        "v1 food",
        "v1 profile",
        "v1 devicestatus",
        "v1 activity",
        "v1 entries",
        "v3 food",
        "v3 profile",
        "v3 settings",
        "v3 devicestatus",
    ];

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task A_document_with_string_numbers_and_mixed_case_names_is_accepted(string name)
    {
        var handler = Build(name);

        var result = await handler.Post(handler.Single);

        StatusOf(result).Should().BeLessThan(300);
        handler.Count().Should().Be(1);
        handler.Bound().Should().BeTrue("the string numbers bind to typed values");
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task An_array_of_such_documents_is_accepted(string name)
    {
        var handler = Build(name);

        var result = await handler.Post($"[{handler.Single},{handler.Single}]");

        StatusOf(result).Should().BeLessThan(300);
        handler.Count().Should().Be(2);
        handler.Bound().Should().BeTrue("the string numbers bind to typed values");
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task A_document_that_does_not_fit_its_model_is_a_bad_request(string name)
    {
        var handler = Build(name);

        var result = await handler.Post(handler.Malformed);

        StatusOf(result).Should().Be(400);
        handler.Count().Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task One_bad_document_refuses_the_whole_array(string name)
    {
        var handler = Build(name);

        var result = await handler.Post($"[{handler.Single},{handler.Malformed}]");

        StatusOf(result).Should().Be(400);
        handler.Count().Should().Be(0);
    }

    [Fact]
    public async Task Treatment_numbers_sent_as_strings_are_stored_as_numbers()
    {
        var created = new List<Treatment>();

        var result = await Treatments(created).CreateTreatments(Json(
            """{"eventType":"Meal Bolus","created_at":"2026-09-30T01:00:00.000Z","carbs":"30","insulin":"2.00"}"""));

        result.Result.Should().BeOfType<OkObjectResult>();
        var treatment = created.Should().ContainSingle().Subject;
        treatment.Carbs.Should().Be(30);
        treatment.Insulin.Should().Be(2);
    }

    [Fact]
    public async Task A_v3_food_update_with_a_document_that_does_not_fit_is_a_bad_request()
    {
        var controller = WithContext(new LegacyV3.FoodController(
            FoodRepository([]), Mock.Of<IDocumentProcessingService>(), NullLogger<LegacyV3.FoodController>.Instance));

        var result = await controller.UpdateFood("x", Json("""{"Name":"Apple","Carbs":"abc","date":1700000000000}"""));

        StatusOf(result).Should().Be(400);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[{\"app\":\"AAPS\",\"device\":\"d\"},null]")]
    public async Task A_v3_devicestatus_array_with_an_element_that_is_not_an_object_is_a_bad_request(string body)
    {
        var handler = Build("v3 devicestatus");

        var result = await handler.Post(body);

        StatusOf(result).Should().Be(400);
        handler.Count().Should().Be(0);
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult { StatusCode: { } code } => code,
        StatusCodeResult status => status.StatusCode,
        _ => 200,
    };

    private static IActionResult Unwrap<T>(ActionResult<T> result) =>
        result.Result ?? new OkObjectResult(result.Value);

    private static Handler Build(string name) => name switch
    {
        "v1 treatments" => TreatmentsHandler(),
        "v1 food" => V1FoodHandler(),
        "v1 profile" => V1ProfileHandler(),
        "v1 devicestatus" or "v3 devicestatus" => DeviceStatusHandler(name),
        "v1 activity" => ActivityHandler(),
        "v1 entries" => EntriesHandler(),
        "v3 food" => V3FoodHandler(),
        "v3 profile" => V3ProfileHandler(),
        "v3 settings" => SettingsHandler(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static Handler TreatmentsHandler()
    {
        var created = new List<Treatment>();
        var controller = Treatments(created);
        return new Handler(
            """{"EventType":"Meal Bolus","Created_At":"2026-09-30T01:00:00.000Z","Carbs":"30"}""",
            """{"EventType":"Meal Bolus","Carbs":"abc"}""",
            async body => Unwrap(await controller.CreateTreatments(Json(body))),
            () => created.Count,
            () => created.All(t => t.Carbs == 30));
    }

    private static Handler V1FoodHandler()
    {
        var foods = new List<Food>();
        var controller = WithContext(new LegacyV1.FoodController(FoodRepository(foods), NullLogger<LegacyV1.FoodController>.Instance));
        return new Handler(
            """{"Name":"Apple","Carbs":"12"}""",
            """{"Name":"Apple","Carbs":"abc"}""",
            async body => Unwrap(await controller.CreateFood(Json(body))),
            () => foods.Count,
            () => foods.All(f => f.Carbs == 12));
    }

    private static Handler V3FoodHandler()
    {
        var foods = new List<Food>();
        var controller = WithContext(new LegacyV3.FoodController(
            FoodRepository(foods), Mock.Of<IDocumentProcessingService>(), NullLogger<LegacyV3.FoodController>.Instance));
        return new Handler(
            """{"Name":"Apple","Carbs":"12","date":1700000000000}""",
            """{"Name":"Apple","Carbs":"abc","date":1700000000000}""",
            async body => await controller.CreateFood(Json(body)),
            () => foods.Count,
            () => foods.All(f => f.Carbs == 12));
    }

    private static Handler V1ProfileHandler()
    {
        var profiles = new List<Profile>();
        var controller = WithContext(new LegacyV1.ProfileController(
            Mock.Of<IProfileProjectionService>(), ProfileWriter(profiles), NullLogger<LegacyV1.ProfileController>.Instance));
        return new Handler(
            $$"""{"DefaultProfile":"Default","Mills":{{Millis}}}""",
            """{"DefaultProfile":"Default","Store":"x"}""",
            async body => Unwrap(await controller.CreateProfiles(Json(body))),
            () => profiles.Count,
            () => profiles.All(p => p.Mills == 1700000000000));
    }

    private static Handler V3ProfileHandler()
    {
        var profiles = new List<Profile>();
        var controller = WithContext(new LegacyV3.ProfileController(
            Mock.Of<IProfileProjectionService>(),
            ProfileWriter(profiles),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<LegacyV3.ProfileController>.Instance));
        return new Handler(
            $$"""{"DefaultProfile":"Default","Mills":{{Millis}}}""",
            """{"DefaultProfile":"Default","Store":"x"}""",
            async body => await controller.CreateProfile(Json(body)),
            () => profiles.Count,
            () => profiles.All(p => p.Mills == 1700000000000));
    }

    private static Handler SettingsHandler()
    {
        var settings = new List<Settings>();
        var repository = new Mock<ISettingsRepository>();
        repository
            .Setup(r => r.CreateSettingsAsync(It.IsAny<IEnumerable<Settings>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Settings>, CancellationToken>((s, _) => settings.AddRange(s))
            .ReturnsAsync([]);
        var controller = WithContext(new LegacyV3.SettingsController(
            repository.Object, Mock.Of<IDocumentProcessingService>(), NullLogger<LegacyV3.SettingsController>.Instance));
        return new Handler(
            $$"""{"Key":"units","Mills":{{Millis}}}""",
            """{"Key":"units","Mills":"abc"}""",
            async body => await controller.CreateSettings(Json(body)),
            () => settings.Count,
            () => settings.All(s => s.Mills == 1700000000000));
    }

    private static Handler ActivityHandler()
    {
        var activities = new List<Activity>();
        var service = new Mock<IActivityService>();
        service
            .Setup(s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Activity>, CancellationToken>((a, _) => activities.AddRange(a))
            .ReturnsAsync([]);
        var controller = WithContext(new LegacyV1.ActivityController(
            service.Object, Mock.Of<IActivityDecomposer>(), NullLogger<LegacyV1.ActivityController>.Instance,
            TimeProvider.System));
        controller.HttpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>();
        return new Handler(
            $$"""{"Type":"walk","Duration":"30","Mills":{{Millis}}}""",
            """{"Type":"walk","Duration":"abc"}""",
            async body => Unwrap(await controller.CreateActivities(Json(body))),
            () => activities.Count,
            () => activities.All(a => a.Duration == 30));
    }

    private static Handler EntriesHandler()
    {
        var entries = new List<Entry>();
        var service = new Mock<IEntryService>();
        service
            .Setup(s => s.CheckForDuplicateEntriesAsync(
                It.IsAny<IReadOnlyList<EntryDuplicateProbe>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntryDuplicateProbe> probes, CancellationToken _) =>
                probes.Select(_ => (Entry?)null).ToArray());
        service
            .Setup(s => s.CreateEntriesAsync(
                It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Entry>, WriteOrigin, CancellationToken>((e, _, _) => entries.AddRange(e))
            .ReturnsAsync([]);
        var controller = WithContext(new LegacyV1.EntriesController(
            service.Object,
            Passthrough(),
            Mock.Of<ICanonicalAlertEvaluator>(),
            TimeProvider.System,
            NullLogger<LegacyV1.EntriesController>.Instance));
        return new Handler(
            $$"""{"Type":"sgv","Sgv":"120","Mills":{{Millis}}}""",
            """{"type":{}}""",
            async body => Unwrap(await controller.CreateEntries(Json(body))),
            () => entries.Count,
            () => entries.All(e => e.Sgv == 120));
    }

    private static Handler DeviceStatusHandler(string name)
    {
        var statuses = new List<DeviceStatus>();
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DecomposeAsync(
                It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceStatus, string?, WriteOrigin, CancellationToken>((ds, _, _, _) => statuses.Add(ds))
            .ReturnsAsync(new DecompositionResult());
        var projection = new DeviceStatusProjectionService(
            Mock.Of<IDeviceStatusGroupRepository>(),
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            NullLogger<DeviceStatusProjectionService>.Instance);
        var single = $$"""{"app":"AAPS","Device":"openaps://phone","Mills":{{Millis}}}""";
        var malformed = """{"app":"AAPS","Device":"openaps://phone","Mills":"abc"}""";

        if (name == "v1 devicestatus")
        {
            var v1 = WithContext(new LegacyV1.DeviceStatusController(
                projection,
                decomposer.Object,
                Mock.Of<IWriteSideEffects>(),
                Mock.Of<IDataEventSink<DeviceStatus>>(),
                TimeProvider.System,
                NullLogger<LegacyV1.DeviceStatusController>.Instance));
            return new Handler(single, malformed, async body => Unwrap(await v1.CreateDeviceStatus(Json(body))), () => statuses.Count, () => statuses.All(s => s.Device == "openaps://phone"));
        }

        var v3 = WithContext(new LegacyV3.DeviceStatusController(
            projection,
            decomposer.Object,
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<LegacyV3.DeviceStatusController>.Instance));
        return new Handler(single, malformed, async body => await v3.CreateDeviceStatus(Json(body)), () => statuses.Count, () => statuses.All(s => s.Device == "openaps://phone"));
    }

    private static LegacyV1.TreatmentsController Treatments(List<Treatment> created)
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Treatment>, CancellationToken>((t, _) => created.AddRange(t))
            .ReturnsAsync((IEnumerable<Treatment> t, CancellationToken _) => new BulkWrite<Treatment>(t.ToList(), 0));
        return WithContext(new LegacyV1.TreatmentsController(
            service.Object,
            new DocumentProcessingService(NullLogger<DocumentProcessingService>.Instance),
            TimeProvider.System,
            NullLogger<LegacyV1.TreatmentsController>.Instance));
    }

    private static IFoodRepository FoodRepository(List<Food> foods)
    {
        var repository = new Mock<IFoodRepository>();
        repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Food>, CancellationToken>((f, _) => foods.AddRange(f))
            .ReturnsAsync((IEnumerable<Food> f, CancellationToken _) => f);
        return repository.Object;
    }

    private static IProfileWriteService ProfileWriter(List<Profile> profiles)
    {
        var writer = new Mock<IProfileWriteService>();
        writer
            .Setup(w => w.CreateProfilesAsync(It.IsAny<IEnumerable<Profile>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Profile>, CancellationToken>((p, _) => profiles.AddRange(p))
            .ReturnsAsync((IEnumerable<Profile> p, CancellationToken _) => p);
        return writer.Object;
    }

    private static IDocumentProcessingService Passthrough()
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Returns((IEnumerable<Entry> entries) => entries);
        return processing.Object;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
