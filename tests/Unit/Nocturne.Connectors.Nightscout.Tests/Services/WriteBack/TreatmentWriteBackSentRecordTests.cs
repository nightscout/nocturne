using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
/// What treatment write-back keeps of each treatment upstream accepted: the fingerprint of each
/// document it went as, which the pull of that document will compare. Nothing is kept for a send upstream refused,
/// so the record still matches what upstream holds.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentWriteBackSentRecordTests
{
    private const string At = "2026-09-30T10:00:00.000Z";
    private const string Key = "6a1f00000000000000000001";
    private static readonly Guid RecordId = Guid.Parse("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f");

    private readonly List<WriteBackSentRecord> _recorded = [];

    /// <summary>Each record added as being sent, and how many requests upstream had been sent before it.</summary>
    private readonly List<(WriteBackSentRecord Record, int RequestsBefore)> _sending = [];

    private int _requests;

    private NightscoutTreatmentWriteBackSink Sink(HttpMessageHandler upstream)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret-12345",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50,
        });
        var records = new Mock<IWriteBackSentRecords>();
        records.Setup(r => r.RecordSentAsync(It.IsAny<IReadOnlyList<WriteBackSentRecord>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<WriteBackSentRecord> sent, CancellationToken _) => _recorded.AddRange(sent))
            .Returns(Task.CompletedTask);
        records.Setup(r => r.RecordSendingAsync(It.IsAny<IReadOnlyList<WriteBackSentRecord>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<WriteBackSentRecord> sending, CancellationToken _) =>
                _sending.AddRange(sending.Select(r => (r, _requests))))
            .Returns(Task.CompletedTask);
        return new NightscoutTreatmentWriteBackSink(
            new HttpClient(new Counting(this) { InnerHandler = upstream }), loader.Object, new NightscoutCircuitBreaker(), records.Object,
            NullLogger<NightscoutTreatmentWriteBackSink>.Instance);
    }

    private static Treatment Meal(double? preBolus) => new()
    {
        Id = RecordId.ToString(),
        LegacyId = Key,
        RecordId = RecordId,
        EventType = "Meal Bolus",
        CreatedAt = At,
        Insulin = 2,
        Carbs = 30,
        PreBolus = preBolus,
        Notes = "lunch",
        DataSource = "careportal",
    };

    private static string FingerprintOf(JsonObject served) =>
        WriteBackFingerprint.Of(JsonSerializer.Deserialize<Treatment>(served.ToJsonString(), UpstreamIdentityJson.ReadOptions)!);

    [Fact]
    public async Task ATreatmentUpstreamAccepted_KeepsTheFingerprintOfTheDocumentItHolds()
    {
        var upstream = new PreBolusSplittingNightscout();

        await Sink(upstream).OnCreatedAsync([Meal(preBolus: null)]);

        _recorded.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WriteBackSentRecord(Key, RecordId, [FingerprintOf(upstream.Documents.Single())]));
    }

    [Fact]
    public async Task AMealSentAsItsBolusAndItsCarbs_KeepsBothDocumentsFingerprints()
    {
        var upstream = new PreBolusSplittingNightscout();

        await Sink(upstream).OnUpdatedAsync(Meal(preBolus: 15));

        var bolus = upstream.Documents.Single(d => d["insulin"] is not null);
        var carbs = upstream.Documents.Single(d => d["carbs"] is not null);
        _recorded.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WriteBackSentRecord(Key, RecordId, [FingerprintOf(bolus), FingerprintOf(carbs)]),
            o => o.WithStrictOrdering());
        FingerprintOf(bolus).Should().NotBe(FingerprintOf(carbs));
    }

    [Fact]
    public async Task ASendUpstreamRefused_KeepsNothing()
    {
        await Sink(new Refusing()).OnCreatedAsync([Meal(preBolus: null)]);

        _recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEdit_IsAddedAsBeingSentBeforeAnythingGoesUpstream_AsTheDocumentsItIsSentAs()
    {
        var upstream = new PreBolusSplittingNightscout();

        await Sink(upstream).OnUpdatedAsync(Meal(preBolus: 15));

        _sending.Should().ContainSingle().Which.RequestsBefore.Should().Be(0);
        _sending.Single().Record.Should().BeEquivalentTo(_recorded.Single(), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task ACreate_IsAddedAsBeingSentBeforeItGoesUpstream_AlsoWhenUpstreamRefusesIt()
    {
        await Sink(new Refusing()).OnCreatedAsync([Meal(preBolus: 15)]);

        _sending.Should().ContainSingle().Which.RequestsBefore.Should().Be(0);
        _sending.Single().Record.Documents.Should().HaveCount(2);
        _recorded.Should().BeEmpty();
    }

    private sealed class Counting(TreatmentWriteBackSentRecordTests test) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("refused") });
    }
}
