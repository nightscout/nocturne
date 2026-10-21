using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// A written-back treatment must leave upstream exactly one document, carrying its carbs under the
/// id the connector's pull matches back to the record, however Nightscout splits a document carrying
/// a <c>preBolus</c> (<see cref="PreBolusSplittingNightscout"/>).
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentPreBolusWriteBackTests
{
    private const string BolusTime = "2026-09-30T10:00:00.000Z";
    private const string Key = "6a1f00000000000000000001";

    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret-12345",
        WriteBackEnabled = true,
        WriteBackBatchSize = 50,
    };

    private NightscoutTreatmentWriteBackSink Sink(PreBolusSplittingNightscout upstream)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_config);
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream) { BaseAddress = new Uri(_config.Url) },
            loader.Object,
            new NightscoutCircuitBreaker(new FakeTimeProvider(DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture))),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private static Treatment MealBolus(double? carbs, double preBolus, string eventType = "Meal Bolus", string id = Key) => new()
    {
        Id = id,
        EventType = eventType,
        CreatedAt = BolusTime,
        Mills = DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
        Insulin = 2,
        Carbs = carbs,
        PreBolus = preBolus,
        Notes = "lunch",
        DataSource = "careportal",
    };

    /// <summary>What a write-back sent before preBolus was left out: the treatment as it stands.</summary>
    private static JsonArray SentWithPreBolus(double carbs, double preBolus, string eventType = "Meal Bolus") => JsonNode.Parse($$"""
        [{"_id":"{{Key}}","identifier":"{{Key}}","eventType":"{{eventType}}","created_at":"{{BolusTime}}","carbs":{{carbs}},"insulin":2,"preBolus":{{preBolus}},"notes":"lunch"}]
        """)!.AsArray();

    private static string? Str(JsonObject document, string name) => document[name]?.GetValue<string>();

    /// <summary>The copy the pull matches to the record: by _id up to 15.0.6, by identifier from 15.0.7.</summary>
    private static string? KeyOf(JsonObject document, bool v15_0_8) => Str(document, v15_0_8 ? "identifier" : "_id");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_fake_splits_a_preBolus_document_as_Nightscout_does(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        upstream.Post(SentWithPreBolus(carbs: 30, preBolus: 15));

        upstream.Documents.Should().HaveCount(2);
        var bolus = upstream.Documents[0];
        KeyOf(bolus, v15_0_8).Should().Be(Key);
        bolus["carbs"].Should().BeNull();
        bolus["insulin"]!.GetValue<double>().Should().Be(2);

        var carbs = upstream.Documents[1];
        Str(carbs, "_id").Should().NotBe(Key);
        carbs["identifier"].Should().BeNull();
        Str(carbs, "created_at").Should().Be("2026-09-30T10:15:00.000Z");
        Str(carbs, "eventType").Should().Be("Meal Bolus");
        carbs["carbs"]!.GetValue<double>().Should().Be(30);
        Str(carbs, "notes").Should().Be("lunch");
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(false, -10)]
    [InlineData(true, 15)]
    [InlineData(true, -10)]
    public async Task A_created_treatment_with_carbs_and_a_preBolus_leaves_one_document_carrying_its_carbs(
        bool v15_0_8, double preBolus)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus)]);

        var copy = upstream.Documents.Should().ContainSingle().Subject;
        KeyOf(copy, v15_0_8).Should().Be(Key);
        Str(copy, "created_at").Should().Be(BolusTime);
        copy["carbs"]!.GetValue<double>().Should().Be(30);
        copy["insulin"]!.GetValue<double>().Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_holding_a_preBolus_treatment_leaves_one_document_per_treatment(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var plain = MealBolus(carbs: 12, preBolus: 0, eventType: "Carb Correction", id: "6a1f00000000000000000002");

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15), plain]);

        upstream.Documents.Select(d => KeyOf(d, v15_0_8)).Should().BeEquivalentTo([Key, plain.Id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_created_treatment_with_a_preBolus_and_no_carbs_leaves_one_document(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: null, preBolus: 15, eventType: "Bolus Wizard")]);

        upstream.Documents.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_edited_treatment_with_carbs_and_a_preBolus_keeps_its_carbs_upstream(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        await Sink(upstream).OnUpdatedAsync(MealBolus(carbs: 45, preBolus: 20, eventType: "Bolus Wizard"));

        upstream.Documents.Should().ContainSingle().Which["carbs"]!.GetValue<double>().Should().Be(45);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Writing_again_a_treatment_an_earlier_write_back_split_removes_the_split_off_carbs(
        bool v15_0_8, bool edit)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(SentWithPreBolus(carbs: 30, preBolus: 15));
        var treatment = MealBolus(carbs: 35, preBolus: 15);

        if (edit)
            await Sink(upstream).OnUpdatedAsync(treatment);
        else
            await Sink(upstream).OnCreatedAsync([treatment]);

        var copy = upstream.Documents.Should().ContainSingle().Subject;
        KeyOf(copy, v15_0_8).Should().Be(Key);
        copy["carbs"]!.GetValue<double>().Should().Be(35);
        copy["preBolus"].Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Documents_at_the_split_slot_that_are_not_a_split_copy_are_kept(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(JsonNode.Parse("""
            [{"eventType":"Carb Correction","created_at":"2026-09-30T10:15:00.000Z","carbs":10,"enteredBy":"careportal"},
             {"identifier":"aaps-1","eventType":"Meal Bolus","created_at":"2026-09-30T10:15:00.000Z","carbs":12,"enteredBy":"aaps"}]
            """)!.AsArray());

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);

        upstream.Documents.Should().HaveCount(3);
    }
}
