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
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// A treatment deleted in Nocturne loses the copies write-back left upstream, and nothing else: no
/// document another client stored there, whatever id it shares with the record.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentWriteBackDeleteTests
{
    private const string BolusTime = "2026-09-30T10:00:00.000Z";
    private const string Key = "6a1f00000000000000000001";
    private const string UploaderId = "7c2d5a10-3b4e-4f6a-9b8c-1d2e3f4a5b6c";
    private static readonly Guid RecordId = Guid.Parse("019a0000-0000-7000-8000-000000000001");

    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret-12345",
        WriteBackEnabled = true,
        WriteBackBatchSize = 50,
    };

    private NightscoutCircuitBreaker _breaker = null!;

    private NightscoutTreatmentWriteBackSink Sink(HttpMessageHandler upstream)
    {
        var loader = Loader(_config);
        _breaker = new NightscoutCircuitBreaker(new FakeTimeProvider(DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture)));
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream) { BaseAddress = new Uri(_config.Url) },
            loader.Object,
            _breaker,
            Mock.Of<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private static Treatment Meal(string legacyId = Key, double? preBolus = null, string dataSource = DataSources.Careportal) => new()
    {
        Id = legacyId,
        LegacyId = legacyId,
        RecordId = RecordId,
        EventType = "Meal Bolus",
        CreatedAt = BolusTime,
        Mills = DateTimeOffset.Parse(BolusTime, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
        Insulin = 2,
        Carbs = 30,
        PreBolus = preBolus,
        Notes = "lunch",
        DataSource = dataSource,
    };

    private static JsonObject Unrelated() => JsonNode.Parse("""
        {"_id":"66b000000000000000000099","identifier":"someone-else","eventType":"Correction Bolus","created_at":"2026-09-30T09:00:00.000Z","insulin":1}
        """)!.AsObject();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_written_back_treatment_deleted_in_Nocturne_is_removed_upstream(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Documents.Add(Unrelated());
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([Meal()]);
        upstream.Documents.Should().HaveCount(2);

        await sink.DeleteUpstreamAsync(Meal(), [Key]);

        upstream.Documents.Should().ContainSingle().Which["identifier"]!.GetValue<string>().Should().Be("someone-else");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_meal_write_back_split_in_two_loses_both_documents(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        var sink = Sink(upstream);
        await sink.OnCreatedAsync([Meal(preBolus: 15)]);
        upstream.Documents.Should().HaveCount(2);

        await sink.DeleteUpstreamAsync(Meal(preBolus: 15), [Key]);

        upstream.Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_meal_Nightscout_split_when_an_earlier_write_back_sent_its_preBolus_loses_both_documents(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(JsonNode.Parse($$"""
            [{"_id":"{{Key}}","identifier":"{{Key}}","eventType":"Meal Bolus","created_at":"{{BolusTime}}","carbs":30,"insulin":2,"preBolus":15,"notes":"lunch"}]
            """)!.AsArray());
        upstream.Documents.Should().HaveCount(2);

        await Sink(upstream).DeleteUpstreamAsync(Meal(preBolus: 15), [Key]);

        upstream.Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_copy_an_uploader_sent_under_its_own_id_stays_upstream(bool v15_0_8)
    {
        var upstream = new PreBolusSplittingNightscout(v15_0_8);
        upstream.Post(JsonNode.Parse($$"""
            [{"_id":"{{UploaderId}}","identifier":"{{UploaderId}}","eventType":"Meal Bolus","created_at":"{{BolusTime}}","carbs":30,"insulin":2,"enteredBy":"uploader"}]
            """)!.AsArray());

        await Sink(upstream).DeleteUpstreamAsync(Meal(UploaderId), [UploaderId]);

        upstream.Documents.Should().ContainSingle();
    }

    [Fact]
    public async Task A_treatment_the_connector_pulled_sends_nothing()
    {
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(Meal(dataSource: DataSources.NightscoutConnector), [Key]);

        upstream.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_is_sent_while_write_back_is_off()
    {
        _config.WriteBackEnabled = false;
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(Meal(), [Key]);

        upstream.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task A_treatment_with_no_id_sends_nothing_rather_than_an_unbounded_delete()
    {
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(new Treatment { EventType = "Note", CreatedAt = BolusTime, DataSource = DataSources.Careportal }, [""]);

        upstream.RequestCount.Should().Be(0);
    }

    /// <summary>Each query parameter of <paramref name="uri"/>, unescaped, in the order it was sent.</summary>
    private static List<(string Name, string Value)> Parameters(Uri uri) =>
    [
        .. uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Select(p => (Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p[1]))),
    ];

    /// <summary>The whole query of a delete by <paramref name="identifiers"/>: no other filter.</summary>
    private static List<(string Name, string Value)> IdentifierDelete(params string[] identifiers) =>
    [
        .. identifiers.Select((identifier, i) => ($"find[identifier][$in][{i}]", identifier)),
        ("find[created_at][$gte]", "1970-01-01T00:00:00.000Z"),
        ("count", "10"),
    ];

    [Fact]
    public async Task A_uuid_legacy_id_is_deleted_by_its_coerced_forms_and_never_as_the_uploader_sent_it()
    {
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(Meal(UploaderId), [UploaderId]);

        upstream.Methods.Should().Equal(HttpMethod.Delete);
        upstream.Uris[0].AbsolutePath.Should().Be("/api/v1/treatments");
        var carbs = PreBolusSplit.WriteBackCarbsId(UploaderId);
        Parameters(upstream.Uris[0]).Should().Equal(IdentifierDelete(
            "7c2d5a103b4e4f6a9b8c1d2e",
            "019a00000000700080000000",
            RecordId.ToString(),
            MongoObjectId.Coerce(carbs)!,
            carbs));
    }

    [Fact]
    public async Task A_24_hex_legacy_id_is_deleted_by_that_id_which_write_back_stores_its_copy_under()
    {
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(Meal(), [Key]);

        upstream.Methods.Should().Equal(HttpMethod.Delete);
        upstream.Uris[0].AbsolutePath.Should().Be("/api/v1/treatments");
        var carbs = PreBolusSplit.WriteBackCarbsId(Key);
        Parameters(upstream.Uris[0]).Should().Equal(IdentifierDelete(
            Key,
            "019a00000000700080000000",
            RecordId.ToString(),
            MongoObjectId.Coerce(carbs)!,
            carbs));
    }

    [Fact]
    public async Task The_copy_Nightscout_split_off_is_deleted_by_its_own_id()
    {
        const string copyId = "66c000000000000000000042";
        var upstream = new RecordingHttpMessageHandler
        {
            Respond = uri => uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""[{"_id":"{{copyId}}","created_at":"2026-09-30T10:15:00.000Z","eventType":"Meal Bolus","carbs":30,"notes":"lunch"}]"""),
                }
                : new HttpResponseMessage(HttpStatusCode.OK),
        };

        await Sink(upstream).DeleteUpstreamAsync(Meal(preBolus: 15), [Key]);

        upstream.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Delete, HttpMethod.Delete);
        upstream.Uris[1].AbsolutePath.Should().Be($"/api/v1/treatments/{copyId}");
        upstream.Uris[1].Query.Should().BeEmpty();
        upstream.Uris[2].AbsolutePath.Should().Be("/api/v1/treatments");
    }

    [Fact]
    public async Task Only_the_legacy_ids_named_lose_their_copies()
    {
        const string carbsHalf = "6a1f000000000000000000c2";
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(Meal(), [carbsHalf]);

        upstream.Methods.Should().Equal(HttpMethod.Delete);
        var carbs = PreBolusSplit.WriteBackCarbsId(carbsHalf);
        Parameters(upstream.Uris[0]).Should().Equal(IdentifierDelete(carbsHalf, MongoObjectId.Coerce(carbs)!, carbs));
    }

    [Fact]
    public async Task No_legacy_id_named_sends_nothing()
    {
        var upstream = new RecordingHttpMessageHandler();

        await Sink(upstream).DeleteUpstreamAsync(Meal(), []);

        upstream.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://nightscout.example.com")]
    public async Task An_unusable_url_is_not_thrown(string url)
    {
        _config.Url = url;
        var upstream = new RecordingHttpMessageHandler();
        var sink = new NightscoutTreatmentWriteBackSink(
            new HttpClient(upstream), Loader(_config).Object, new NightscoutCircuitBreaker(), Mock.Of<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        var delete = () => sink.DeleteUpstreamAsync(Meal(), [Key]);

        await delete.Should().NotThrowAsync();
        upstream.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task A_configuration_that_fails_to_load_is_not_thrown()
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new UnreadableConfiguration());
        var sink = new NightscoutTreatmentWriteBackSink(
            new HttpClient(new RecordingHttpMessageHandler()), loader.Object, new NightscoutCircuitBreaker(), Mock.Of<IWriteBackSentRecords>(),
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

        var delete = () => sink.DeleteUpstreamAsync(Meal(), [Key]);

        await delete.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_cancel_the_caller_asked_for_still_cancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var sink = Sink(new RecordingHttpMessageHandler());

        var delete = () => sink.DeleteUpstreamAsync(Meal(), [Key], cancelled.Token);

        await delete.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>> Loader(NightscoutConnectorConfiguration config)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(config);
        return loader;
    }

    private sealed class UnreadableConfiguration() : System.Data.Common.DbException("The connection broke.");

    [Fact]
    public async Task A_delete_upstream_refuses_is_not_thrown_and_counts_against_the_breaker()
    {
        var upstream = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError);
        var sink = Sink(upstream);

        for (var i = 0; i < 5; i++)
        {
            var delete = () => sink.DeleteUpstreamAsync(Meal(), [Key]);
            await delete.Should().NotThrowAsync();
        }

        _breaker.IsOpen.Should().BeTrue();
        await sink.DeleteUpstreamAsync(Meal(), [Key]);
        upstream.RequestCount.Should().Be(5);
    }
}
