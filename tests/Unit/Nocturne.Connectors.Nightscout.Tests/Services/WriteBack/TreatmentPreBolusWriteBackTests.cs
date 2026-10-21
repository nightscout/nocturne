using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// A written-back treatment must leave upstream exactly one document, carrying its carbs under the
/// id the connector's pull matches back to the record. Nightscout splits a document carrying a
/// non-zero <c>preBolus</c> (<see cref="PreBolusSplittingNightscout"/>).
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentPreBolusWriteBackTests
{
    private const string BolusTime = "2026-09-30T10:00:00.000Z";

    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret-12345",
        WriteBackEnabled = true,
        WriteBackBatchSize = 50,
    };

    private readonly PreBolusSplittingNightscout _upstream = new();

    private NightscoutTreatmentWriteBackSink Sink()
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_config);
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(_upstream) { BaseAddress = new Uri(_config.Url) },
            loader.Object,
            new NightscoutCircuitBreaker(new FakeTimeProvider(DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture))),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private static Treatment MealBolus(double? carbs, double preBolus, string eventType = "Meal Bolus") => new()
    {
        Id = "6a1f00000000000000000001",
        EventType = eventType,
        CreatedAt = BolusTime,
        Mills = DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
        Insulin = 2,
        Carbs = carbs,
        PreBolus = preBolus,
        Notes = "lunch",
        DataSource = "nocturne",
    };

    [Fact]
    public void The_fake_splits_a_preBolus_document_as_Nightscout_does()
    {
        _upstream.Post(JsonNode.Parse($$"""
            [{"_id":"6a1f00000000000000000001","eventType":"Meal Bolus","created_at":"{{BolusTime}}","carbs":30,"insulin":2,"preBolus":15,"notes":"lunch"}]
            """)!.AsArray());

        _upstream.Documents.Should().HaveCount(2);
        var bolus = _upstream.Documents[0];
        bolus["_id"]!.GetValue<string>().Should().Be("6a1f00000000000000000001");
        bolus["carbs"].Should().BeNull();
        bolus["insulin"]!.GetValue<double>().Should().Be(2);

        var carbs = _upstream.Documents[1];
        carbs["_id"]!.GetValue<string>().Should().NotBe("6a1f00000000000000000001");
        carbs["identifier"].Should().BeNull();
        carbs["created_at"]!.GetValue<string>().Should().Be("2026-09-30T10:15:00.000Z");
        carbs["eventType"]!.GetValue<string>().Should().Be("Meal Bolus");
        carbs["carbs"]!.GetValue<double>().Should().Be(30);
        carbs["notes"]!.GetValue<string>().Should().Be("lunch");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(-10)]
    public async Task A_created_treatment_with_carbs_and_a_preBolus_leaves_one_document_carrying_its_carbs(double preBolus)
    {
        var treatment = MealBolus(carbs: 30, preBolus);

        await Sink().OnCreatedAsync([treatment]);

        _upstream.Documents.Should().ContainSingle();
        var copy = _upstream.Documents[0];
        copy["_id"]!.GetValue<string>().Should().Be(treatment.Id);
        copy["created_at"]!.GetValue<string>().Should().Be(BolusTime);
        copy["carbs"]!.GetValue<double>().Should().Be(30);
        copy["insulin"]!.GetValue<double>().Should().Be(2);
    }

    [Fact]
    public async Task A_batch_holding_a_preBolus_treatment_leaves_one_document_per_treatment()
    {
        var withPreBolus = MealBolus(carbs: 30, preBolus: 15);
        var plain = MealBolus(carbs: 12, preBolus: 0, eventType: "Carb Correction");
        plain.Id = "6a1f00000000000000000002";

        await Sink().OnCreatedAsync([withPreBolus, plain]);

        _upstream.Documents.Select(d => d["_id"]!.GetValue<string>())
            .Should().BeEquivalentTo([withPreBolus.Id, plain.Id]);
    }

    [Fact]
    public async Task A_created_treatment_with_a_preBolus_and_no_carbs_leaves_one_document()
    {
        await Sink().OnCreatedAsync([MealBolus(carbs: null, preBolus: 15, eventType: "Bolus Wizard")]);

        _upstream.Documents.Should().ContainSingle();
    }

    [Fact]
    public async Task An_edited_treatment_with_carbs_and_a_preBolus_keeps_its_carbs_upstream()
    {
        var treatment = MealBolus(carbs: 45, preBolus: 20, eventType: "Bolus Wizard");

        await Sink().OnUpdatedAsync(treatment);

        _upstream.Documents.Should().ContainSingle();
        _upstream.Documents[0]["carbs"]!.GetValue<double>().Should().Be(45);
    }

    /// <summary>
    /// The v1 treatment writes of Nightscout's <c>lib/server/treatments.js</c> (15.0.x) as far as
    /// <c>preBolus</c> reaches them. <c>prepareData</c> moves the carbs of a document with a non-zero
    /// <c>preBolus</c> into <c>preBolusCarbs</c> and deletes them; <c>upsert</c> (POST, an array
    /// one document at a time) then upserts a second document,
    /// <c>{created_at: created_at + preBolus minutes, eventType, carbs: preBolusCarbs, notes}</c>, by
    /// <c>created_at</c> and <c>eventType</c> under a minted <c>_id</c>, carbs or not. <c>save</c>
    /// (PUT) runs <c>prepareData</c> and writes no second document, so the carbs are lost.
    /// </summary>
    private sealed class PreBolusSplittingNightscout : HttpMessageHandler
    {
        private int _minted;

        public List<JsonObject> Documents { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/treatments");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;

            if (request.Method == HttpMethod.Post)
                Post(body is JsonArray array ? array : new JsonArray(body.DeepClone()));
            else if (request.Method == HttpMethod.Put)
                Save(body.AsObject());
            else
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
        }

        public void Post(JsonArray documents)
        {
            foreach (var node in documents)
            {
                var obj = node!.DeepClone().AsObject();
                var (createdAt, preBolusCarbs) = PrepareData(obj);
                var eventType = obj["eventType"]?.GetValue<string>();
                Upsert(createdAt, eventType, obj);

                if (obj["preBolus"] is { } preBolus)
                {
                    var pbCreatedAt = Iso(DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture)
                        .AddMinutes(preBolus.GetValue<double>()));
                    var pbTreat = new JsonObject
                    {
                        ["created_at"] = pbCreatedAt,
                        ["eventType"] = eventType,
                        ["carbs"] = preBolusCarbs?.DeepClone() ?? JsonValue.Create(""),
                    };
                    if (obj["notes"] is { } notes)
                        pbTreat["notes"] = notes.DeepClone();
                    Upsert(pbCreatedAt, eventType, pbTreat);
                }
            }
        }

        private void Save(JsonObject obj)
        {
            PrepareData(obj);
            var id = obj["_id"]?.GetValue<string>();
            var stored = Documents.FindIndex(d => id is not null && d["_id"]?.GetValue<string>() == id);
            obj["_id"] ??= Mint();
            if (stored >= 0)
                Documents[stored] = obj;
            else
                Documents.Add(obj);
        }

        private void Upsert(string createdAt, string? eventType, JsonObject obj)
        {
            var stored = Documents.FindIndex(d =>
                d["created_at"]?.GetValue<string>() == createdAt && d["eventType"]?.GetValue<string>() == eventType);
            if (stored >= 0)
            {
                obj["_id"] = Documents[stored]["_id"]!.DeepClone();
                Documents[stored] = obj;
                return;
            }

            obj["_id"] ??= Mint();
            Documents.Add(obj);
        }

        private static (string CreatedAt, JsonNode? PreBolusCarbs) PrepareData(JsonObject obj)
        {
            var createdAt = Iso(DateTimeOffset.Parse(obj["created_at"]!.GetValue<string>(), CultureInfo.InvariantCulture));
            obj["created_at"] = createdAt;

            var preBolus = Number(obj["preBolus"]);
            var carbs = Number(obj["carbs"]);
            JsonNode? preBolusCarbs = null;
            if (preBolus is not (0 or double.NaN) && carbs is not (0 or double.NaN))
            {
                preBolusCarbs = JsonValue.Create(carbs);
                obj.Remove("carbs");
            }

            foreach (var (field, value) in new[] { ("carbs", carbs), ("insulin", Number(obj["insulin"])), ("preBolus", preBolus) })
            {
                if (value is 0 or double.NaN)
                    obj.Remove(field);
                else if (obj.ContainsKey(field))
                    obj[field] = value;
            }

            return (createdAt, preBolusCarbs);
        }

        private static double Number(JsonNode? node) => node?.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>(),
            JsonValueKind.String => double.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : node.GetValue<string>().Length == 0 ? 0 : double.NaN,
            JsonValueKind.Null => 0,
            _ => double.NaN,
        };

        private static string Iso(DateTimeOffset at) =>
            at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        private string Mint() => $"{++_minted:x24}";
    }
}
