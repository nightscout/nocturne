using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// A written-back meal with a <c>preBolus</c> must leave upstream its bolus at its own time and its
/// carbs at <c>created_at + preBolus</c>, as a v1 POST stores it, each under the id the connector's
/// pull matches back to the record, however Nightscout splits a document carrying a <c>preBolus</c>
/// (<see cref="PreBolusSplittingNightscout"/>). Any other treatment leaves one document carrying its carbs.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentPreBolusWriteBackTests
{
    private const string BolusTime = "2026-09-30T10:00:00.000Z";
    private const string Key = "6a1f00000000000000000001";
    private static readonly string CarbsKey = MongoObjectId.Coerce(PreBolusSplit.WriteBackCarbsId(Key))!;
    private static readonly string V1CarbsKey = MongoObjectId.Coerce(PreBolusSplit.CarbsId(Key))!;

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
            Mock.Of<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private static Treatment MealBolus(
        double? carbs, double preBolus, string eventType = "Meal Bolus", string id = Key, double? insulin = 2) => new()
    {
        Id = Guid.NewGuid().ToString(),
        LegacyId = id,
        EventType = eventType,
        CreatedAt = BolusTime,
        Mills = DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
        Insulin = insulin,
        Carbs = carbs,
        PreBolus = preBolus,
        Notes = "lunch",
        DataSource = "careportal",
    };

    /// <summary>What a write-back sent before preBolus was left out: the treatment as it stands.</summary>
    private static JsonArray SentWithPreBolus(string carbs, string preBolus, string eventType) => JsonNode.Parse($$"""
        [{"_id":"{{Key}}","identifier":"{{Key}}","eventType":"{{eventType}}","created_at":"{{BolusTime}}","carbs":{{carbs}},"insulin":2,"preBolus":{{preBolus}},"notes":"lunch"}]
        """)!.AsArray();

    private static JsonArray SentWithPreBolus(double carbs, double preBolus, string eventType = "Meal Bolus") =>
        SentWithPreBolus(carbs.ToString(CultureInfo.InvariantCulture), preBolus.ToString(CultureInfo.InvariantCulture), eventType);

    private static string? Str(JsonObject document, string name) => document[name]?.GetValue<string>();

    /// <summary>The copy the pull matches to the record: by _id up to 15.0.6, by identifier from 15.0.7.</summary>
    private static string? KeyOf(JsonObject document, bool v15_0_8) => Str(document, v15_0_8 ? "identifier" : "_id");

    private static void ShouldHoldTheMealSplit(
        PreBolusSplittingNightscout upstream, bool v15_0_8, double carbs, string carbsAt = "2026-09-30T10:15:00.000Z")
    {
        upstream.Documents.Select(d => KeyOf(d, v15_0_8)).Should().BeEquivalentTo([Key, CarbsKey]);
        var bolus = upstream.Documents.Single(d => KeyOf(d, v15_0_8) == Key);
        Str(bolus, "created_at").Should().Be(BolusTime);
        bolus["carbs"].Should().BeNull();
        bolus["insulin"]!.GetValue<double>().Should().Be(2);
        bolus["preBolus"].Should().BeNull();

        var meal = upstream.Documents.Single(d => KeyOf(d, v15_0_8) == CarbsKey);
        Str(meal, "identifier").Should().Be(CarbsKey);
        Str(meal, "created_at").Should().Be(carbsAt);
        Str(meal, "eventType").Should().Be("Meal Bolus");
        meal["carbs"]!.GetValue<double>().Should().Be(carbs);
        meal["insulin"].Should().BeNull();
        Str(meal, "notes").Should().Be("lunch");
    }

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
    public async Task A_created_meal_with_a_preBolus_leaves_its_bolus_at_its_time_and_its_carbs_at_the_offset(
        bool v15_0_8, double preBolus)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus)]);

        ShouldHoldTheMealSplit(upstream, v15_0_8, carbs: 30, carbsAt: preBolus > 0 ? "2026-09-30T10:15:00.000Z" : "2026-09-30T09:50:00.000Z");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_created_treatment_with_carbs_and_a_preBolus_that_is_not_a_meal_leaves_one_document_carrying_its_carbs(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15, eventType: "Bolus Wizard")]);

        var copy = upstream.Documents.Should().ContainSingle().Subject;
        KeyOf(copy, v15_0_8).Should().Be(Key);
        Str(copy, "created_at").Should().Be(BolusTime);
        copy["carbs"]!.GetValue<double>().Should().Be(30);
    }

    /// <summary>A v1 POST stores such a meal split already, and each half goes up as it is.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_halves_of_a_meal_a_v1_POST_split_go_up_unsplit(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);

        await Sink(upstream).OnCreatedAsync(V1Split());

        upstream.Documents.Select(d => KeyOf(d, v15_0_8)).Should().BeEquivalentTo([Key, V1CarbsKey]);
        upstream.Documents.Single(d => d["carbs"] is not null)["created_at"]!.GetValue<string>().Should().Be("2026-09-30T10:15:00.000Z");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_holding_a_preBolus_treatment_leaves_one_document_per_treatment(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var plain = MealBolus(carbs: 12, preBolus: 0, eventType: "Carb Correction", id: "6a1f00000000000000000002");

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15), plain]);

        upstream.Documents.Select(d => KeyOf(d, v15_0_8)).Should().BeEquivalentTo([Key, CarbsKey, plain.LegacyId]);
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

    /// <summary>
    /// An edit lands each half on the copy upstream holds; a meal written back whole before it was
    /// split loses its carbs to the new carbs document.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_edited_meal_with_a_preBolus_updates_both_documents(bool v15_0_8, bool writtenWhole)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        if (writtenWhole)
            upstream.Post(JsonNode.Parse($$"""
                [{"_id":"{{Key}}","identifier":"{{Key}}","eventType":"Meal Bolus","created_at":"{{BolusTime}}","carbs":30,"insulin":2,"notes":"lunch"}]
                """)!.AsArray());
        else
            await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);

        await sink.OnUpdatedAsync(MealBolus(carbs: 45, preBolus: 20));

        ShouldHoldTheMealSplit(upstream, v15_0_8, carbs: 45, carbsAt: "2026-09-30T10:20:00.000Z");
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

        ShouldHoldTheMealSplit(upstream, v15_0_8, carbs: 35);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Documents_at_the_split_slot_that_are_not_a_split_copy_are_kept(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(JsonNode.Parse("""
            [{"eventType":"Carb Correction","created_at":"2026-09-30T10:15:00.000Z","carbs":10,"enteredBy":"careportal"},
             {"identifier":"aaps-1","eventType":"Bolus Wizard","created_at":"2026-09-30T10:15:00.000Z","carbs":12,"enteredBy":"aaps"}]
            """)!.AsArray());

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15, eventType: "Bolus Wizard")]);

        upstream.Documents.Should().HaveCount(3);
    }

    /// <summary>
    /// An edit after which the meal no longer splits leaves upstream the one document the treatment
    /// now is, without the carbs the earlier write split off. Up to 15.0.6 an edit of the event type
    /// is refused (<see cref="A_refused_bolus_edit_leaves_the_carbs_it_split_off"/>).
    /// </summary>
    [Theory]
    [InlineData(false, "preBolus removed")]
    [InlineData(true, "preBolus removed")]
    [InlineData(false, "insulin removed")]
    [InlineData(true, "insulin removed")]
    [InlineData(true, "now a correction")]
    public async Task An_edit_that_stops_a_meal_splitting_removes_the_carbs_it_split_off(bool v15_0_8, string edit)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        var edited = edit switch
        {
            "preBolus removed" => MealBolus(carbs: 60, preBolus: 0),
            "insulin removed" => MealBolus(carbs: 60, preBolus: 15, insulin: null),
            _ => MealBolus(carbs: null, preBolus: 15, eventType: "Correction Bolus"),
        };

        await sink.OnUpdatedAsync(edited);

        var copy = upstream.Documents.Should().ContainSingle().Subject;
        KeyOf(copy, v15_0_8).Should().Be(Key);
        Str(copy, "created_at").Should().Be(BolusTime);
        copy["carbs"]?.GetValue<double>().Should().Be(edited.Carbs);
    }

    /// <summary>The bolus and the carbs record a v1 POST splits <see cref="MealBolus"/> into, as reads serve them.</summary>
    private static IReadOnlyList<Treatment> V1Split(double carbs = 30)
    {
        var meal = MealBolus(carbs, preBolus: 15);
        meal.Id = Key;
        var halves = PreBolusSplit.Split(meal, _ => { });
        halves[1].LegacyId = halves[1].Id;
        return halves;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_edit_that_empties_a_meals_carbs_removes_the_carbs_it_split_off(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);

        await sink.OnUpdatedAsync(MealBolus(carbs: null, preBolus: 15));

        var copy = upstream.Documents.Should().ContainSingle().Subject;
        KeyOf(copy, v15_0_8).Should().Be(Key);
        copy["carbs"].Should().BeNull();
    }

    /// <summary>The carbs of a meal a v1 POST split are a record of their own, which an edit of its bolus leaves.</summary>
    [Theory]
    [InlineData(false, "preBolus removed")]
    [InlineData(true, "preBolus removed")]
    [InlineData(false, "now a correction")]
    [InlineData(true, "now a correction")]
    public async Task An_edit_of_the_bolus_of_a_meal_a_v1_POST_split_keeps_its_carbs_upstream(bool v15_0_8, string edit)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync(V1Split());

        await sink.OnUpdatedAsync(edit == "preBolus removed"
            ? MealBolus(carbs: null, preBolus: 0)
            : MealBolus(carbs: null, preBolus: 15, eventType: "Correction Bolus"));

        upstream.Documents.Single(d => KeyOf(d, v15_0_8) == V1CarbsKey)["carbs"]!.GetValue<double>().Should().Be(30);
        upstream.Documents.Should().HaveCount(2);
    }

    /// <summary>
    /// A one-record meal posted again through v1 is stored split, and announced as the service
    /// announces it: the new carbs record created, then the bolus re-uploaded as an update.
    /// </summary>
    private static async Task PostAgainThroughV1Async(NightscoutTreatmentWriteBackSink sink, double carbs = 30)
    {
        var halves = V1Split(carbs);
        await sink.OnCreatedAsync([halves[1]]);
        await sink.OnUpdatedAsync(halves[0]);
    }

    private static void ShouldHoldTheV1Split(PreBolusSplittingNightscout upstream, bool v15_0_8, double carbs = 30)
    {
        upstream.Documents.Select(d => KeyOf(d, v15_0_8)).Should().BeEquivalentTo([Key, V1CarbsKey]);
        upstream.Documents.Single(d => KeyOf(d, v15_0_8) == Key)["carbs"].Should().BeNull();
        var meal = upstream.Documents.Single(d => KeyOf(d, v15_0_8) == V1CarbsKey);
        Str(meal, "created_at").Should().Be("2026-09-30T10:15:00.000Z");
        meal["carbs"]!.GetValue<double>().Should().Be(carbs);
    }

    /// <summary>
    /// Up to 15.0.6 the carbs record's POST upserts onto the carbs the meal's write-back split off at
    /// the same time, and is refused; those are removed and the POST sent again. From 15.0.7 it is
    /// matched by its identifier, and the bolus update removes the split-off carbs.
    /// </summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task A_written_back_meal_posted_again_through_v1_with_a_preBolus_keeps_its_carbs_upstream(
        bool v15_0_8, int posts)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        upstream.Methods.Clear();

        await PostAgainThroughV1Async(sink);

        ShouldHoldTheV1Split(upstream, v15_0_8);
        upstream.Methods.Count(m => m == HttpMethod.Post).Should().Be(posts);
    }

    /// <summary>
    /// A meal posted again while write-back was off stores its carbs record unsent, so when it is
    /// posted again with write-back on the carbs record is an update, and its create is refused as a
    /// create is up to 15.0.6.
    /// </summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task A_stored_carbs_record_of_a_written_back_meal_never_sent_keeps_its_carbs_upstream(
        bool v15_0_8, int posts)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        upstream.Methods.Clear();
        var halves = V1Split();

        await sink.OnUpdatedAsync(halves[1]);
        await sink.OnUpdatedAsync(halves[0]);

        ShouldHoldTheV1Split(upstream, v15_0_8);
        upstream.Methods.Count(m => m == HttpMethod.Post).Should().Be(posts);
    }

    [Fact]
    public async Task A_stored_carbs_record_refused_again_after_the_clear_is_sent_no_more_than_twice()
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8: false);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        upstream.Methods.Clear();
        upstream.KeepOnDelete = true;

        await sink.OnUpdatedAsync(V1Split()[1]);

        upstream.Methods.Count(m => m == HttpMethod.Post).Should().Be(2);
        upstream.Methods.Count(m => m == HttpMethod.Delete).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_written_back_meal_posted_again_through_v1_without_a_preBolus_leaves_one_document(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);

        await sink.OnUpdatedAsync(MealBolus(carbs: 30, preBolus: 0));

        var copy = upstream.Documents.Should().ContainSingle().Subject;
        KeyOf(copy, v15_0_8).Should().Be(Key);
        copy["carbs"]!.GetValue<double>().Should().Be(30);
        copy["insulin"]!.GetValue<double>().Should().Be(2);
    }

    /// <summary>A meal whose write-back stored only its bolus upstream before the meal was posted again.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_meal_posted_again_through_v1_before_its_write_back_finished_leaves_its_bolus_and_its_carbs(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        upstream.Documents.RemoveAll(d => KeyOf(d, v15_0_8) == CarbsKey);

        await PostAgainThroughV1Async(sink);

        ShouldHoldTheV1Split(upstream, v15_0_8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_meal_posted_again_through_v1_a_second_time_edits_its_carbs_in_place(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        await PostAgainThroughV1Async(sink);
        var halves = V1Split(carbs: 45);

        await sink.OnUpdatedAsync(halves[0]);
        await sink.OnUpdatedAsync(halves[1]);

        ShouldHoldTheV1Split(upstream, v15_0_8, carbs: 45);
    }

    /// <summary>A delete is not written back, so upstream keeps both documents.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_meal_deleted_after_it_was_posted_again_through_v1_stays_upstream(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        await PostAgainThroughV1Async(sink);
        upstream.Methods.Clear();

        foreach (var half in V1Split())
            await sink.OnDeletedAsync(half);

        ShouldHoldTheV1Split(upstream, v15_0_8);
        upstream.Methods.Should().BeEmpty();
    }

    /// <summary>A POST that fails for any reason other than 15.0.6's immutable <c>_id</c> clears nothing and is not sent again.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.InternalServerError, """{"status":500,"message":"Mongo Error","description":[{"driver":true,"name":"MongoError","index":0,"code":11000}]}""")]
    [InlineData(HttpStatusCode.BadGateway, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    public async Task A_carbs_record_create_failing_otherwise_leaves_the_split_off_carbs_and_is_sent_once(
        HttpStatusCode status, string? body)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8: false);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        upstream.Methods.Clear();
        upstream.FailPostsWith = status;
        upstream.FailPostsBody = body;

        await sink.OnCreatedAsync([V1Split()[1]]);

        upstream.Methods.Should().Equal(HttpMethod.Post);
        upstream.Documents.Select(d => KeyOf(d, false)).Should().BeEquivalentTo([Key, CarbsKey]);
    }

    /// <summary>A refusal still there once the split-off carbs are cleared is not cleared and sent again a second time.</summary>
    [Fact]
    public async Task A_carbs_record_refused_again_after_the_clear_is_sent_no_more_than_twice()
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8: false);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        upstream.Methods.Clear();
        upstream.KeepOnDelete = true;

        await sink.OnCreatedAsync([V1Split()[1]]);

        upstream.Methods.Count(m => m == HttpMethod.Post).Should().Be(2);
        upstream.Methods.Count(m => m == HttpMethod.Delete).Should().Be(1);
    }

    /// <summary>
    /// Only the carbs write-back split off the meal are removed for a refused carbs record: another
    /// uploader's meal at that time keeps refusing it up to 15.0.6.
    /// </summary>
    [Fact]
    public async Task A_carbs_record_refused_by_another_uploaders_meal_leaves_that_meal_up_to_15_0_6()
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8: false);
        upstream.Post(JsonNode.Parse("""
            [{"identifier":"aaps-1","eventType":"Meal Bolus","created_at":"2026-09-30T10:15:00.000Z","carbs":12,"enteredBy":"aaps"}]
            """)!.AsArray());

        await Sink(upstream).OnCreatedAsync([V1Split()[1]]);

        upstream.Documents.Should().ContainSingle().Which["carbs"]!.GetValue<double>().Should().Be(12);
        upstream.Methods.Should().NotContain(HttpMethod.Delete);
    }

    [Fact]
    public async Task An_edit_of_a_meal_that_still_splits_writes_its_carbs_in_place_from_15_0_7()
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8: true);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        var carbsId = Str(upstream.Documents.Single(d => KeyOf(d, true) == CarbsKey), "_id");

        await sink.OnUpdatedAsync(MealBolus(carbs: 45, preBolus: 20));

        ShouldHoldTheMealSplit(upstream, true, carbs: 45, carbsAt: "2026-09-30T10:20:00.000Z");
        Str(upstream.Documents.Single(d => KeyOf(d, true) == CarbsKey), "_id").Should().Be(carbsId);
    }

    /// <summary>A bolus edit the upstream refuses leaves the carbs as they were.</summary>
    [Fact]
    public async Task A_refused_bolus_edit_leaves_the_carbs_it_split_off()
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8: false);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);
        var moved = MealBolus(carbs: 60, preBolus: 0);
        moved.Mills += 60_000;
        moved.CreatedAt = "2026-09-30T10:01:00.000Z";

        await sink.OnUpdatedAsync(moved);

        upstream.Documents.Single(d => KeyOf(d, false) == CarbsKey)["carbs"]!.GetValue<double>().Should().Be(30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fractional_preBolus_finds_the_copy_an_earlier_write_back_split_off(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(SentWithPreBolus(carbs: 30, preBolus: 7.5));

        await Sink(upstream).OnUpdatedAsync(MealBolus(carbs: 35, preBolus: 7.5));

        ShouldHoldTheMealSplit(upstream, v15_0_8, carbs: 35, carbsAt: "2026-09-30T10:07:30.000Z");
    }

    /// <summary>
    /// Up to 15.0.6 the carbs POST upserts by time and event type onto another uploader's meal at that
    /// instant, and is refused for the <c>_id</c> it would change, as Nightscout's own split of the meal
    /// would have overwritten it; from 15.0.7 both are kept.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_uploaders_meal_at_the_carbs_time_is_kept(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(JsonNode.Parse("""
            [{"identifier":"aaps-1","eventType":"Meal Bolus","created_at":"2026-09-30T10:15:00.000Z","carbs":12,"enteredBy":"aaps"}]
            """)!.AsArray());

        await Sink(upstream).OnCreatedAsync([MealBolus(carbs: 30, preBolus: 15)]);

        upstream.Documents.Should().ContainSingle(d => Str(d, "identifier") == "aaps-1")
            .Which["carbs"]!.GetValue<double>().Should().Be(12);
        upstream.Documents.Select(d => KeyOf(d, v15_0_8)).Should().Contain(v15_0_8 ? [Key, CarbsKey] : [Key]).And.HaveCount(v15_0_8 ? 3 : 2);
    }
}
