using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Health;

/// <summary>
/// The response shapes the heart rate, step count and body weight routes have always answered
/// with, now that all three read them from shared controller bodies. These are the wire contract
/// three generated artefacts are built from — the NSwag TypeScript client, the Zod schemas and the
/// SvelteKit remote functions — so each is pinned here rather than left to the shared base.
/// </summary>
[Trait("Category", "Unit")]
public class HealthRecordResponseContractTests
{
    private const string MongoId = "507f1f77bcf86cd799439011";

    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<IStepCountService> _stepCounts = new();
    private readonly Mock<IBodyWeightService> _bodyWeights = new();

    private HeartRateController HeartRate() =>
        new(_heartRates.Object) { ProblemDetailsFactory = new PassThroughProblemDetailsFactory() };

    private StepCountController StepCount() =>
        new(_stepCounts.Object) { ProblemDetailsFactory = new PassThroughProblemDetailsFactory() };

    private BodyWeightController BodyWeight() =>
        new(_bodyWeights.Object) { ProblemDetailsFactory = new PassThroughProblemDetailsFactory() };

    // ── A count nobody supplied ─────────────────────────────────────

    [Fact]
    public async Task Heart_rate_list_without_a_count_reads_ten()
    {
        await HeartRate().GetHeartRates();

        _heartRates.Verify(s => s.GetHeartRatesAsync(10, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Step_count_list_without_a_count_reads_ten()
    {
        await StepCount().GetStepCounts();

        _stepCounts.Verify(s => s.GetStepCountsAsync(10, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Body_weight_list_without_a_count_reads_ten()
    {
        await BodyWeight().GetBodyWeights();

        _bodyWeights.Verify(s => s.GetBodyWeightsAsync(10, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Heart_rate_range_read_forwards_the_skip_it_was_given()
    {
        var from = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc);

        await HeartRate().GetHeartRates(count: 50, skip: 3, from: from, to: from.AddDays(1));

        _heartRates.Verify(
            s => s.GetHeartRatesByDateRangeAsync(from, from.AddDays(1), 50, 3, It.IsAny<CancellationToken>()),
            Times.Once,
            "paging through a range is the only way to read past the ceiling");
    }

    // ── A route id that is not a GUID ───────────────────────────────

    [Fact]
    public async Task Get_by_id_hands_a_mongo_object_id_to_the_service_verbatim()
    {
        _heartRates
            .Setup(s => s.GetHeartRateByIdAsync(MongoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeartRate { Id = MongoId, Bpm = 61 });

        var result = await HeartRate().GetHeartRate(MongoId);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<HeartRate>()
            .Which.Id.Should().Be(MongoId, "a legacy id reaches the service unparsed");
    }

    [Fact]
    public async Task Get_by_an_unknown_id_answers_404_naming_the_record_type_and_the_id()
    {
        var result = await HeartRate().GetHeartRate("nope");

        Problem(result.Result, 404).Detail
            .Should().Be("Heart rate record with ID nope not found");
    }

    // ── Delete answers 200 with a message, not 204 ──────────────────

    [Fact]
    public async Task Delete_answers_200_with_a_message_rather_than_204()
    {
        _heartRates
            .Setup(s => s.DeleteHeartRateAsync(MongoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await HeartRate().DeleteHeartRate(MongoId);

        var ok = result.Should().BeOfType<OkObjectResult>(
            "a 204 carries no body, and the generated client would stop reading one").Subject;
        ok.StatusCode.Should().Be(200);
        Message(ok).Should().Be("Heart rate record deleted successfully");
    }

    [Fact]
    public async Task Body_weight_delete_answers_200_with_its_own_message()
    {
        _bodyWeights
            .Setup(s => s.DeleteBodyWeightAsync(MongoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await BodyWeight().DeleteBodyWeight(MongoId);

        Message(result.Should().BeOfType<OkObjectResult>().Subject)
            .Should().Be("Body weight record deleted successfully");
    }

    [Fact]
    public async Task Delete_of_a_missing_record_answers_404()
    {
        var result = await StepCount().DeleteStepCount("gone");

        Problem(result, 404).Detail.Should().Be("Step count record with ID gone not found");
    }

    // ── Create takes an array and answers a bare array ──────────────

    [Fact]
    public async Task Create_maps_every_request_in_the_batch_and_answers_a_bare_array()
    {
        var taken = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.FromHours(10));
        _heartRates
            .Setup(s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<HeartRate> models, CancellationToken _) => models);

        var result = await HeartRate().CreateHeartRates([
            new UpsertHeartRateRequest { Timestamp = taken, UtcOffset = 600, Bpm = 61, Accuracy = 2, App = "prelude" },
            new UpsertHeartRateRequest { Timestamp = taken, Bpm = 74 },
        ]);

        var created = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<HeartRate>>().Subject.ToList();

        created.Should().HaveCount(2, "the response is the array itself, not a paginated envelope");
        created[0].Timestamp.Should().Be(taken.UtcDateTime);
        created[0].Bpm.Should().Be(61);
        created[0].Accuracy.Should().Be(2);
        created[0].UtcOffset.Should().Be(600);
        created[0].EnteredBy.Should().Be("prelude", "the request's App names who entered the record");
    }

    [Fact]
    public async Task Create_with_an_empty_array_answers_400_and_never_reaches_the_service()
    {
        var result = await StepCount().CreateStepCounts([]);

        Problem(result.Result, 400).Detail.Should().Be("Step count data is required");
        _stepCounts.Verify(
            s => s.CreateStepCountsAsync(It.IsAny<IEnumerable<StepCount>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── The batch route takes an array of records ───────────────────

    [Fact]
    public async Task Batch_create_reaches_the_service_with_every_record()
    {
        var written = CaptureBodyWeights();

        var result = await BodyWeight().CreateBodyWeights([
            new BodyWeight { Mills = 1_781_000_000_000, WeightKg = 80.5m },
            new BodyWeight { Mills = 1_781_000_060_000, WeightKg = 81.0m },
        ]);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<BodyWeight>>()
            .Which.Should().HaveCount(2);
        written().Select(w => w.WeightKg).Should().Equal([80.5m, 81.0m]);
    }

    [Fact]
    public async Task Batch_create_with_no_body_answers_400()
    {
        var result = await BodyWeight().CreateBodyWeights(null!);

        Problem(result.Result, 400).Detail.Should().Be("Body weight data is required");
    }

    /// <summary>
    /// The batch route's declared body type is what every published SDK's method signature is
    /// generated from, so the shape the wire accepts is pinned through MVC's own binding rather
    /// than left to the signature.
    /// </summary>
    [Fact]
    public async Task Batch_create_binds_an_array_body_and_refuses_a_bare_record()
    {
        var bodyType = typeof(BodyWeightController)
            .GetMethod(nameof(BodyWeightController.CreateBodyWeights))!
            .GetParameters()[0]
            .ParameterType;
        const string record = """{"weightKg":80.5,"mills":1781000000000}""";

        (await BindBodyAsync(bodyType, $"[{record}]")).HasError.Should().BeFalse();
        (await BindBodyAsync(bodyType, record)).HasError.Should().BeTrue();
    }

    /// <summary>Runs MVC's JSON body binding, which is what answers 400 before an action runs.</summary>
    private static Task<InputFormatterResult> BindBodyAsync(Type bodyType, string json)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return new SystemTextJsonInputFormatter(new JsonOptions(), NullLogger<SystemTextJsonInputFormatter>.Instance)
            .ReadAsync(new InputFormatterContext(
                httpContext,
                modelName: string.Empty,
                modelState: new ModelStateDictionary(),
                metadata: new EmptyModelMetadataProvider().GetMetadataForType(bodyType),
                readerFactory: (stream, encoding) => new StreamReader(stream, encoding)));
    }

    // ── Update ──────────────────────────────────────────────────────

    [Fact]
    public async Task Update_maps_the_request_onto_the_model_it_hands_the_service()
    {
        StepCount? written = null;
        _stepCounts
            .Setup(s => s.UpdateStepCountAsync(MongoId, It.IsAny<StepCount>(), It.IsAny<CancellationToken>()))
            .Callback((string _, StepCount model, CancellationToken _) => written = model)
            .ReturnsAsync((string _, StepCount model, CancellationToken _) => model);

        await StepCount().UpdateStepCount(MongoId, new UpsertStepCountRequest
        {
            Timestamp = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            Metric = 1200,
            Source = 1,
            Device = "watch",
        });

        written.Should().NotBeNull();
        written!.Metric.Should().Be(1200);
        written.Source.Should().Be(1);
        written.Device.Should().Be("watch");
    }

    [Fact]
    public async Task Update_of_a_missing_record_answers_404()
    {
        var result = await StepCount().UpdateStepCount("gone", new UpsertStepCountRequest());

        Problem(result.Result, 404).Detail.Should().Be("Step count record with ID gone not found");
    }

    /// <summary>Records what the batch route handed the service, and echoes it back as the response.</summary>
    private Func<List<BodyWeight>> CaptureBodyWeights()
    {
        List<BodyWeight> written = [];
        _bodyWeights
            .Setup(s => s.CreateBodyWeightsAsync(It.IsAny<IEnumerable<BodyWeight>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BodyWeight> models, CancellationToken _) =>
            {
                written = models.ToList();
                return written;
            });
        return () => written;
    }

    private static ProblemDetails Problem(ActionResult? result, int statusCode)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(statusCode);
        return objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
    }

    private static string? Message(OkObjectResult ok) =>
        ok.Value?.GetType().GetProperty("message")?.GetValue(ok.Value) as string;

    /// <summary>Stands in for the MVC-registered factory, which needs a request scope.</summary>
    private sealed class PassThroughProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null,
            string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };
    }
}
