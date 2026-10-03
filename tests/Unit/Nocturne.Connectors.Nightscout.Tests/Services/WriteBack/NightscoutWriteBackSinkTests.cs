using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services.WriteBack;

/// <summary>
/// Covers the shared write-back base class through the entry sink — the path a live
/// cutover exercises hardest. During a cutover Nocturne and the tenant's legacy
/// Nightscout run side by side, so every request shape, skip rule, and failure
/// reaction here is visible in the tenant's old instance.
/// </summary>
[Trait("Category", "Unit")]
public class NightscoutWriteBackSinkTests
{
    // SHA-1 of "test-secret-12345" — the hash Nightscout v1 expects in the api-secret header.
    private const string ExpectedApiSecretHash = "deb6894e47fb5cd2abea8e47f1c4399ac1ff7d11";

    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret-12345",
        WriteBackEnabled = true,
        WriteBackBatchSize = 50
    };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private NightscoutCircuitBreaker Breaker => _breaker ??= new NightscoutCircuitBreaker(_time);
    private NightscoutCircuitBreaker? _breaker;

    private static Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>> CreateLoader(
        NightscoutConnectorConfiguration config)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        return loader;
    }

    private NightscoutEntryWriteBackSink CreateSink(
        RecordingHttpMessageHandler handler,
        IConnectorConfigurationLoader<NightscoutConnectorConfiguration>? loader = null,
        string? clientBaseAddress = "https://nightscout.example.com")
    {
        var httpClient = new HttpClient(handler);
        if (clientBaseAddress is not null)
            httpClient.BaseAddress = new Uri(clientBaseAddress);

        return new NightscoutEntryWriteBackSink(
            httpClient,
            loader ?? CreateLoader(_config).Object,
            Breaker,
            NullLogger<NightscoutEntryWriteBackSink>.Instance);
    }

    private static List<Entry> Entries(int count, string dataSource = "nocturne")
        => Enumerable.Range(1, count)
            .Select(i => new Entry { Id = i.ToString(), Sgv = 100 + i, DataSource = dataSource })
            .ToList();

    /// <summary>
    /// Runs write-back off the test thread under a wall-clock deadline. The batching
    /// loop is index-driven over a synchronous handler, so a stride bug spins without
    /// ever yielding; without this guard the failure mode is a hung CI job instead of
    /// a red test.
    /// </summary>
    private static async Task ShouldFinishPromptly(Func<Task> writeBack)
    {
        var work = Task.Run(writeBack);
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5)));

        first.Should().BeSameAs(
            work,
            "the write-back loop must terminate — it is still sending after 5 seconds");
        await work;
    }

    [Fact]
    public async Task OnCreatedAsync_PostsAJsonArrayToTheV1EntriesEndpoint()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(2));

        handler.RequestCount.Should().Be(1);
        handler.Methods[0].Should().Be(HttpMethod.Post);
        handler.Uris[0].Should().Be(new Uri("https://nightscout.example.com/api/v1/entries"));
        handler.Bodies[0].Should().StartWith("[").And.Contain("\"sgv\":101").And.Contain("\"sgv\":102");
    }

    [Fact]
    public async Task OnCreatedAsync_SendsTheSha1HashedApiSecretHeader()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        handler.ApiSecretHeaders[0].Should().Be(ExpectedApiSecretHash);
    }

    [Fact]
    public async Task OnCreatedAsync_PassesAnAlreadyHashedSecretThroughLowercased()
    {
        _config.ApiSecret = ExpectedApiSecretHash.ToUpperInvariant();
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        handler.ApiSecretHeaders[0].Should().Be(ExpectedApiSecretHash);
    }

    [Fact]
    public async Task OnCreatedAsync_SingleItem_StillPostsAnArray()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        handler.RequestCount.Should().Be(1);
        handler.Methods[0].Should().Be(HttpMethod.Post);
        handler.Bodies[0].Should().StartWith("[");
    }

    [Fact]
    public async Task OnUpdatedAsync_PutsABareObjectNotAnArray()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnUpdatedAsync(new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        handler.RequestCount.Should().Be(1);
        handler.Methods[0].Should().Be(HttpMethod.Put);
        handler.Uris[0].AbsolutePath.Should().Be("/api/v1/entries");
        handler.Bodies[0].Should().StartWith("{");
    }

    /// <summary>
    /// The update path has its own skip check, separate from the create path's
    /// collection filter. Without it, editing a connector-pulled entry in Nocturne
    /// PUTs it back to the legacy instance, which re-pulls it on the next cycle —
    /// a write loop between the two systems during a live cutover.
    /// </summary>
    [Fact]
    public async Task OnUpdatedAsync_SkipsAnEntrySourcedFromTheNightscoutConnector()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnUpdatedAsync(new Entry
        {
            Id = "1",
            Sgv = 120,
            DataSource = DataSources.NightscoutConnector
        });

        handler.RequestCount.Should().Be(0);
    }

    /// <summary>
    /// Same skip check on the single-item create overload, which also bypasses the
    /// collection filter.
    /// </summary>
    [Fact]
    public async Task OnCreatedAsync_SingleItem_SkipsAnEntrySourcedFromTheNightscoutConnector()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(new Entry
        {
            Id = "1",
            Sgv = 120,
            DataSource = DataSources.NightscoutConnector
        });

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task OnDeletedAsync_SendsNothing()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnDeletedAsync(new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task OnCreatedAsync_SendsNothing_WhenWriteBackIsDisabled()
    {
        _config.WriteBackEnabled = false;
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(3));

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task OnCreatedAsync_SendsNothing_WhenEveryItemIsFilteredOut()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(3, DataSources.NightscoutConnector));

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task OnCreatedAsync_SendsOnlyTheItemsNotSourcedFromTheNightscoutConnector()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(
        [
            new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" },
            new Entry { Id = "2", Sgv = 130, DataSource = DataSources.NightscoutConnector }
        ]);

        handler.RequestCount.Should().Be(1);
        handler.Bodies[0].Should().Contain("\"sgv\":120").And.NotContain("\"sgv\":130");
    }

    [Fact]
    public async Task OnCreatedAsync_SendsNothing_WhileTheCircuitBreakerIsOpen()
    {
        for (var i = 0; i < 5; i++)
            Breaker.RecordFailure();
        Breaker.IsOpen.Should().BeTrue();

        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task OnCreatedAsync_ResumesSending_OnceTheRecoveryWindowElapses()
    {
        for (var i = 0; i < 5; i++)
            Breaker.RecordFailure();

        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        _time.Advance(TimeSpan.FromSeconds(60));
        await sut.OnCreatedAsync(Entries(1));

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task OnCreatedAsync_SwallowsServerErrorsAndRecordsThemAsFailures()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError);
        var sut = CreateSink(handler);

        var act = async () =>
        {
            for (var i = 0; i < 5; i++)
                await sut.OnCreatedAsync(Entries(1));
        };

        await act.Should().NotThrowAsync();
        handler.RequestCount.Should().Be(5);
        Breaker.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Pins current behaviour: a 4xx counts toward the breaker exactly like a 5xx.
    /// A permanently misconfigured api-secret (401) or an endpoint the legacy instance
    /// does not implement (404) therefore trips the shared breaker and suspends
    /// write-back for every collection, not just the failing one.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task OnCreatedAsync_ClientErrorsAlsoTripTheBreaker(HttpStatusCode status)
    {
        var handler = new RecordingHttpMessageHandler(status);
        var sut = CreateSink(handler);

        for (var i = 0; i < 5; i++)
            await sut.OnCreatedAsync(Entries(1));

        Breaker.IsOpen.Should().BeTrue(
            "this pins CURRENT behaviour, not desired behaviour: a 4xx is a permanent "
            + "client-side fault, yet it trips the shared breaker exactly like a 5xx. "
            + "Invert this assertion if failure classification is ever narrowed to 5xx "
            + "and transport errors");
    }

    [Fact]
    public async Task OnCreatedAsync_SwallowsTransportExceptionsAndRecordsThemAsFailures()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ThrowFor = _ => new HttpRequestException("connection refused")
        };
        var sut = CreateSink(handler);

        var act = async () =>
        {
            for (var i = 0; i < 5; i++)
                await sut.OnCreatedAsync(Entries(1));
        };

        await act.Should().NotThrowAsync();
        Breaker.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Pins current behaviour: the catch-all in the base sink also swallows
    /// cancellation, so a cancelled request (host shutdown, client disconnect)
    /// is counted as a Nightscout failure and can trip the breaker on its own.
    /// </summary>
    [Fact]
    public async Task OnCreatedAsync_CountsCancellationAsAFailureAndDoesNotPropagateIt()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            for (var i = 0; i < 5; i++)
                await sut.OnCreatedAsync(Entries(1), cts.Token);
        };

        await act.Should().NotThrowAsync();
        handler.RequestCount.Should().Be(0);
        Breaker.IsOpen.Should().BeTrue(
            "this pins CURRENT behaviour, not desired behaviour: the catch-all swallows "
            + "OperationCanceledException, so host shutdown or a client disconnect counts "
            + "as a Nightscout failure and can trip the breaker on its own. Invert this "
            + "assertion if cancellation is ever rethrown instead of counted");
    }

    [Fact]
    public async Task OnCreatedAsync_ASuccessfulSendClosesABreakerThatWasNearlyTripped()
    {
        for (var i = 0; i < 4; i++)
            Breaker.RecordFailure();

        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        Breaker.IsOpen.Should().BeFalse();
        for (var i = 0; i < 4; i++)
            Breaker.RecordFailure();
        Breaker.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task OnCreatedAsync_SplitsIntoBatchesOfTheConfiguredSize()
    {
        _config.WriteBackBatchSize = 2;
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(5));

        handler.RequestCount.Should().Be(3);
        handler.Bodies[0].Should().Contain("\"sgv\":101").And.Contain("\"sgv\":102");
        handler.Bodies[1].Should().Contain("\"sgv\":103").And.Contain("\"sgv\":104");
        handler.Bodies[2].Should().Contain("\"sgv\":105").And.NotContain("\"sgv\":104");
    }

    [Fact]
    public async Task OnCreatedAsync_SendsASingleRequest_WhenTheBatchFitsExactly()
    {
        _config.WriteBackBatchSize = 5;
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(5));

        handler.RequestCount.Should().Be(1);
    }

    /// <summary>
    /// Pins a real hazard: the breaker is consulted once per call, before batching.
    /// A large create therefore keeps hammering an unreachable Nightscout for every
    /// remaining batch even after the breaker has opened.
    /// </summary>
    [Fact]
    public async Task OnCreatedAsync_KeepsSendingEveryBatch_EvenAfterTheBreakerOpensMidLoop()
    {
        _config.WriteBackBatchSize = 1;
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError);
        var sut = CreateSink(handler);

        await ShouldFinishPromptly(() => sut.OnCreatedAsync(Entries(20)));

        handler.RequestCount.Should().Be(
            20,
            "this pins CURRENT behaviour, not desired behaviour: the breaker is consulted "
            + "once per call, before batching, so a large create keeps hammering an "
            + "unreachable Nightscout for every remaining batch. Change this to 5 if the "
            + "breaker is ever re-checked inside the batch loop");
        Breaker.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// A non-positive batch size reaches the sink because the declared minimum of 1
    /// only lands in the UI JSON schema — nothing clamps the value bound from the
    /// per-tenant configuration row. Zero would leave the loop index standing still
    /// and a negative would walk it backwards; both send at the tenant's Nightscout
    /// without end. The stride is clamped to 1, so the loop terminates and every item
    /// is delivered exactly once, one per request.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task OnCreatedAsync_ClampsANonPositiveBatchSizeAndTerminates(int batchSize)
    {
        _config.WriteBackBatchSize = batchSize;
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await ShouldFinishPromptly(() => sut.OnCreatedAsync(Entries(3)));

        handler.RequestCount.Should().Be(3);
        handler.Bodies[0].Should().Contain("\"sgv\":101").And.NotContain("\"sgv\":102");
        handler.Bodies[1].Should().Contain("\"sgv\":102");
        handler.Bodies[2].Should().Contain("\"sgv\":103");
    }

    /// <summary>
    /// Neither delete hook is overridden, so both fall through to the interface's
    /// no-op defaults. Reached through the interface because that is how the
    /// composite sink invokes them.
    /// </summary>
    [Fact]
    public async Task BulkAndPreDeleteHooks_SendNothing()
    {
        var handler = new RecordingHttpMessageHandler();
        IDataEventSink<Entry> sut = CreateSink(handler);

        await sut.OnBulkDeletedAsync(42);
        await sut.BeforeDeleteAsync("1");

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task OnCreatedAsync_PrefixesHttpsWhenTheConfiguredUrlHasNoScheme()
    {
        _config.Url = "nightscout.example.com";
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        handler.Uris[0].Should().Be(new Uri("https://nightscout.example.com/api/v1/entries"));
    }

    [Fact]
    public async Task OnCreatedAsync_KeepsAnExplicitHttpScheme()
    {
        _config.Url = "http://legacy.local:1337";
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        handler.Uris[0].Should().Be(new Uri("http://legacy.local:1337/api/v1/entries"));
    }

    [Fact]
    public async Task OnCreatedAsync_TrimsATrailingSlashFromTheConfiguredUrl()
    {
        _config.Url = "https://nightscout.example.com/";
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);

        await sut.OnCreatedAsync(Entries(1));

        handler.Uris[0].Should().Be(new Uri("https://nightscout.example.com/api/v1/entries"));
    }

    /// <summary>
    /// The typed HttpClient is configured once at startup from the process-wide
    /// connector settings, while the destination is per tenant. The sink must send
    /// to the tenant's configured URL, never to the client's base address.
    /// </summary>
    [Fact]
    public async Task OnCreatedAsync_SendsToTheTenantConfigUrl_NotTheHttpClientBaseAddress()
    {
        _config.Url = "https://tenant-b.example.com";
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler, clientBaseAddress: "https://startup-default.example.com");

        await sut.OnCreatedAsync(Entries(1));

        handler.Uris[0].Host.Should().Be("tenant-b.example.com");
    }

    [Fact]
    public async Task ConfigurationIsLoadedOncePerSinkInstance()
    {
        var loader = CreateLoader(_config);
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler, loader.Object);

        await sut.OnCreatedAsync(Entries(1));
        await sut.OnUpdatedAsync(new Entry { Id = "9", Sgv = 99, DataSource = "nocturne" });

        loader.Verify(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Nightscout 15.0.7 and later upsert an entry by sysTime and type with <c>$set</c>, so any
    /// <c>_id</c> other than the stored reading's is an immutable-field error that aborts the batch.
    /// An entry whose id is not an ObjectId, the id Nightscout keeps, therefore goes out with no
    /// <c>_id</c>; every entry carries its own key verbatim as <c>identifier</c>, for the pull-back
    /// to find the record by.
    /// </summary>
    [Theory]
    [InlineData("dexcom_7f3c2a91", false)]
    [InlineData("5f1a2b3c4d5e6f7a8b9c0d1e", true)]
    [InlineData("5F1A2B3C4D5E6F7A8B9C0D1E", false)]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f", false)]
    [InlineData("0198C2A4-1F3B-7C2D-9E55-6A1B2C3D4E5F", false)]
    public async Task EntryWriteBack_SendsAnIdOnlyForAnObjectIdAndTheRecordsOwnKeyAsItsIdentifier(string id, bool sendsId)
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSink(handler);
        var entry = new Entry { Id = id, Sgv = 120, DataSource = "nocturne" };

        await sut.OnCreatedAsync(new[] { entry });
        await sut.OnUpdatedAsync(entry);

        var expected = new Dictionary<string, string?> { ["identifier"] = id };
        if (sendsId)
            expected["_id"] = id;
        var posted = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[0])[0];
        var put = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]);
        foreach (var body in new[] { posted, put })
            IdKeys(body).Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("loop_status_42")]
    [InlineData("devicestatus-3f1c2a")]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f")]
    public async Task DeviceStatusWriteBack_SendsAnObjectIdAndTheRecordsOwnKeyAsItsIdentifier(string id)
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = new NightscoutDeviceStatusWriteBackSink(
            new HttpClient(handler),
            CreateLoader(_config).Object,
            Breaker,
            NullLogger<NightscoutDeviceStatusWriteBackSink>.Instance);

        await sut.OnCreatedAsync(new[] { new DeviceStatus { Id = id, Device = "loop" } });

        IdKeys(JsonSerializer.Deserialize<JsonElement>(handler.Bodies[0])[0])
            .Should().BeEquivalentTo(new Dictionary<string, string?>
            {
                ["_id"] = MongoObjectId.Coerce(id),
                ["identifier"] = id,
            });
    }

    private NightscoutTreatmentWriteBackSink TreatmentSink(RecordingHttpMessageHandler handler) => new(
        new HttpClient(handler),
        CreateLoader(_config).Object,
        Breaker,
        NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

    /// <summary>
    /// A treatment goes upstream as it always has: <c>_id</c> and <c>identifier</c> both carry the
    /// 24-hex coercion of its key, so an upsert matches the copy an earlier write-back left there by
    /// that <c>identifier</c>. A v1 create carries the key as its id and a v1 edit carries it apart
    /// from the uuid it is served by, and both come to the same value.
    /// </summary>
    [Theory]
    [InlineData("65a1b2c3d4e5f60718293a4b")]
    [InlineData("syn-3a7c0e9f1b2d4c6e")]
    [InlineData("4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f")]
    [InlineData("5F1A2B3C4D5E6F7A8B9C0D1E")]
    public async Task TreatmentCreate_SendsItsCoercedKeyAsIdAndIdentifier(string legacyId)
    {
        var handler = new RecordingHttpMessageHandler();

        await TreatmentSink(handler).OnCreatedAsync(new[]
        {
            new Treatment { Id = legacyId, EventType = "Correction Bolus", Insulin = 1, DataSource = "nocturne" },
            new Treatment { Id = RecordUuid, LegacyId = legacyId, EventType = "Correction Bolus", Insulin = 1, DataSource = "nocturne" },
        });

        var wire = MongoObjectId.Coerce(legacyId);
        wire.Should().MatchRegex("^[0-9a-f]{24}$");
        var sent = JsonSerializer.Deserialize<JsonElement>(handler.Bodies.Single());
        foreach (var body in sent.EnumerateArray())
            IdKeys(body).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = wire, ["identifier"] = wire });
    }

    [Fact]
    public async Task TreatmentCreate_WithNoLegacyId_SendsItsUuidPrefixAsIdAndIdentifier()
    {
        var handler = new RecordingHttpMessageHandler();

        await TreatmentSink(handler).OnCreatedAsync(new[] { new Treatment { Id = RecordUuid, EventType = "Note", DataSource = "nocturne" } });

        var prefix = MongoObjectId.FromGuid(Guid.Parse(RecordUuid));
        IdKeys(JsonSerializer.Deserialize<JsonElement>(handler.Bodies.Single())[0])
            .Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = prefix, ["identifier"] = prefix });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string CreatedAtFloor = "find[created_at][$gte]=1970-01-01T00%3A00%3A00.000Z";

    /// <summary>
    /// A handler answering a find by identifier, a find by <c>_id</c> and a find by
    /// <c>_id</c> <c>$in</c> with the bodies given.
    /// </summary>
    private static RecordingHttpMessageHandler Upstream(string byIdentifier, string byId = "[]", string byStringId = "[]") => new()
    {
        Respond = uri => uri.Query.Contains("find[identifier]", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, byIdentifier)
            : uri.Query.Contains("find[_id][$in]", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, byStringId)
            : uri.Query.Contains("find[_id]", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, byId)
            : new HttpResponseMessage(HttpStatusCode.OK),
    };

    private static Treatment Edit(string? legacyId) => new()
    {
        Id = RecordUuid, LegacyId = legacyId, RecordId = Guid.Parse(RecordUuid), EventType = "Correction Bolus", Insulin = 2, DataSource = "nocturne",
    };

    /// <summary>The last body sent: the one document of a POST, or the document a PUT sent.</summary>
    private static JsonElement Sent(RecordingHttpMessageHandler handler)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[^1]);
        return body.ValueKind == JsonValueKind.Array ? body.EnumerateArray().Single() : body;
    }

    /// <summary>
    /// An edit asks the upstream, in one find, for a copy under any identifier a write-back may have
    /// sent the treatment under (its coerced key, its record's uuid prefix, its raw key, its record's
    /// full uuid), with a <c>created_at</c> bound of its own so Nightscout's default four-day window
    /// does not hide the copy of an older treatment. Finding none, it asks for one under its
    /// <c>_id</c>, then, for a legacy id that is not an ObjectId, under that legacy id as a string
    /// <c>_id</c>; both skip that window. Finding none there either, the edit is sent as a create is:
    /// under both keys, so a later create upserts onto it.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_HeldNowhere_IsLookedForUnderEveryFormThenPostedAsACreate()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var wire = MongoObjectId.Coerce(legacyId)!;
        var prefix = MongoObjectId.FromGuid(Guid.Parse(RecordUuid));
        var handler = Upstream("[]");

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Get, HttpMethod.Get, HttpMethod.Post);
        handler.Uris[0].PathAndQuery.Should().Be(
            $"/api/v1/treatments.json?find[identifier][$in][0]={wire}&find[identifier][$in][1]={prefix}&find[identifier][$in][2]={legacyId}&find[identifier][$in][3]={RecordUuid}&{CreatedAtFloor}&count=10");
        handler.Uris[1].PathAndQuery.Should().Be($"/api/v1/treatments.json?find[_id]={wire}&count=1");
        handler.Uris[2].PathAndQuery.Should().Be($"/api/v1/treatments.json?find[_id][$in][0]={legacyId}&count=1");
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = wire, ["identifier"] = wire });
    }

    /// <summary>
    /// A legacy id that is an ObjectId is its own wire key: the find under <c>_id</c> already asked
    /// for it, so no string <c>_id</c> is looked for.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_HeldNowhere_WithAnObjectIdLegacyId_IsNotLookedForUnderAStringId()
    {
        const string legacyId = "65a1b2c3d4e5f60718293a4b";
        var handler = Upstream("[]");

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Get, HttpMethod.Post);
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = legacyId, ["identifier"] = legacyId });
    }

    /// <summary>
    /// The original of a treatment a Nightscout migration imported from an uploader that sent its own
    /// id is held up to 15.0.6 under that id as a string <c>_id</c>, with no identifier. The edit is
    /// POSTed under it, with the identifier the original holds, else the legacy id: up to 15.0.6 the
    /// POST matches it by time and event type and keeps its <c>_id</c>, and 15.0.7 and later match
    /// the string <c>_id</c> through the identifier's <c>$or</c>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("a3c1f2e4-5b6d-4e7f-8a9b-0c1d2e3f4a5b")]
    public async Task TreatmentEdit_OfAnOriginalHeldUnderItsLegacyIdAsAStringId_IsPostedUnderIt(string? heldIdentifier)
    {
        const string legacyId = "xdrip-3a7c0e9f1b2d4c6e";
        var held = heldIdentifier is null
            ? $$"""[{"_id":"{{legacyId}}"}]"""
            : $$"""[{"_id":"{{legacyId}}","identifier":"{{heldIdentifier}}"}]""";
        var handler = Upstream("[]", byStringId: held);

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Get, HttpMethod.Get, HttpMethod.Post);
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = legacyId, ["identifier"] = heldIdentifier ?? legacyId });
    }

    /// <summary>
    /// Up to v0.2.3 an edit went upstream under the record's full uuid, as both keys, whatever its
    /// legacy id; 15.0.7 and later kept that uuid as the identifier of a copy under an ObjectId of
    /// its own. The edit is PUT onto that copy.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_OfACopyAnEarlierEditLeftUnderTheRecordsFullUuid_IsPutOntoIt()
    {
        var handler = Upstream($$"""[{"_id":"66b0c1d2e3f405162738495a","identifier":"{{RecordUuid}}"}]""");

        await TreatmentSink(handler).OnUpdatedAsync(Edit("65a1b2c3d4e5f60718293a4b"));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Put);
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = "66b0c1d2e3f405162738495a", ["identifier"] = RecordUuid });
    }

    /// <summary>
    /// A copy 15.0.7 or later stored has an ObjectId of its own. The edit is PUT under it with the
    /// identifier the copy was found by, which every version lands on it: 15.0.7 and later by that
    /// identifier, up to 15.0.6 by the ObjectId.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_OfACopyUnderAnObjectIdOfItsOwn_IsPutUnderItWithTheIdentifierItWasFoundBy()
    {
        var prefix = MongoObjectId.FromGuid(Guid.Parse(RecordUuid));
        var handler = Upstream($$"""[{"_id":"66b0c1d2e3f405162738495a","identifier":"{{prefix}}"}]""");

        await TreatmentSink(handler).OnUpdatedAsync(Edit("syn-3a7c0e9f1b2d4c6e"));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Put);
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = "66b0c1d2e3f405162738495a", ["identifier"] = prefix });
    }

    /// <summary>
    /// Of several copies (already a duplicate upstream), the edit goes to the one under the most
    /// current form.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_PrefersTheCopyUnderTheMostCurrentForm()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var wire = MongoObjectId.Coerce(legacyId)!;
        var handler = Upstream($$"""[{"_id":"66b0c1d2e3f4051627384951","identifier":"{{legacyId}}"},{"_id":"66b0c1d2e3f4051627384952","identifier":"{{wire}}"}]""");

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = "66b0c1d2e3f4051627384952", ["identifier"] = wire });
    }

    /// <summary>
    /// A copy whose <c>_id</c> is not an ObjectId was stored by a POST up to 15.0.6, under the string
    /// sent. The edit is POSTed under it, which up to 15.0.6 finds it by time and event type; a PUT
    /// would save under <c>new ObjectID(_id)</c> and store a second copy.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_OfACopyUnderAStringId_IsPostedUnderIt()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var handler = Upstream($$"""[{"_id":"{{legacyId}}","identifier":"{{legacyId}}"}]""");

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Post);
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = legacyId, ["identifier"] = legacyId });
    }

    /// <summary>
    /// A copy found by its identifier but held under another string <c>_id</c> keeps that <c>_id</c>
    /// on the POST: up to 15.0.6 an edit that moved the time or event type then inserts under a taken
    /// <c>_id</c> and is refused, rather than stored beside the copy under a minted one.
    /// </summary>
    [Fact]
    public async Task TreatmentEdit_OfACopyUnderAnotherStringId_IsPostedUnderThatId()
    {
        const string legacyId = "syn-3a7c0e9f1b2d4c6e";
        var handler = Upstream($$"""[{"_id":"loop-override-7","identifier":"{{legacyId}}"}]""");

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Post);
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = "loop-override-7", ["identifier"] = legacyId });
    }

    /// <summary>
    /// A copy served with its identifier as its <c>_id</c> was stored up to 15.0.6, by a POST under
    /// that string or by a PUT under that ObjectId, which a read serves alike. A find by <c>_id</c>
    /// matches only the ObjectId: held so, the edit is PUT under it; else POSTed under the string.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TreatmentEdit_OfACopyUnderItsIdentifierAsItsId_AsksWhetherThatIsAnObjectId(bool heldAsObjectId)
    {
        const string legacyId = "65a1b2c3d4e5f60718293a4b";
        var copy = $$"""[{"_id":"{{legacyId}}","identifier":"{{legacyId}}"}]""";
        var handler = Upstream(copy, byId: heldAsObjectId ? copy : "[]");

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Get, heldAsObjectId ? HttpMethod.Put : HttpMethod.Post);
        handler.Uris[1].PathAndQuery.Should().Be($"/api/v1/treatments.json?find[_id]={legacyId}&count=1");
        IdKeys(Sent(handler)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["_id"] = legacyId, ["identifier"] = legacyId });
    }

    /// <summary>
    /// With no copy under any identifier, a treatment upstream holds under its <c>_id</c> (the
    /// original a Nightscout migration imported) is PUT under it, which every version saves in place.
    /// A PUT replaces the whole document, so an identifier the original holds (a v3 client's, by
    /// which AAPS matches its records) goes out with it; with none, none is sent, since 15.0.7 and
    /// later would match an identifier against nothing there.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("a3c1f2e4-5b6d-4e7f-8a9b-0c1d2e3f4a5b")]
    public async Task TreatmentEdit_HeldUnderItsId_IsPutUnderIt_WithTheIdentifierItHolds(string? heldIdentifier)
    {
        const string legacyId = "65a1b2c3d4e5f60718293a4b";
        var held = heldIdentifier is null
            ? $$"""[{"_id":"{{legacyId}}"}]"""
            : $$"""[{"_id":"{{legacyId}}","identifier":"{{heldIdentifier}}"}]""";
        var handler = Upstream("[]", byId: held);

        await TreatmentSink(handler).OnUpdatedAsync(Edit(legacyId));

        handler.Methods.Should().Equal(HttpMethod.Get, HttpMethod.Get, HttpMethod.Put);
        var expected = new Dictionary<string, string?> { ["_id"] = legacyId };
        if (heldIdentifier is not null)
            expected["identifier"] = heldIdentifier;
        IdKeys(Sent(handler)).Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// An upstream that cannot say where it holds the copy, at either lookup, is sent nothing: a
    /// POST would store a second copy of a treatment it holds under its <c>_id</c> alone, a PUT of
    /// one it holds under its identifier. The failure counts against the circuit breaker.
    /// </summary>
    [Theory]
    [InlineData(0, HttpStatusCode.InternalServerError, "", false)]
    [InlineData(0, HttpStatusCode.Unauthorized, "", false)]
    [InlineData(0, HttpStatusCode.OK, "not json", false)]
    [InlineData(0, HttpStatusCode.OK, """{"status":200}""", false)]
    [InlineData(0, HttpStatusCode.OK, """[{"identifier":"x"}]""", false)]
    [InlineData(0, HttpStatusCode.OK, "", true)]
    [InlineData(1, HttpStatusCode.InternalServerError, "", false)]
    [InlineData(1, HttpStatusCode.OK, "not json", false)]
    [InlineData(1, HttpStatusCode.OK, "", true)]
    [InlineData(2, HttpStatusCode.InternalServerError, "", false)]
    [InlineData(2, HttpStatusCode.OK, """[{"identifier":"x"}]""", false)]
    [InlineData(2, HttpStatusCode.OK, "", true)]
    public async Task TreatmentEdit_WhenTheUpstreamCannotSayWhereItHoldsTheCopy_SendsNothingAndCountsAFailure(
        int failingRead, HttpStatusCode status, string body, bool throws)
    {
        var reads = 0;
        var handler = new RecordingHttpMessageHandler
        {
            RespondTo = (method, _) => method != HttpMethod.Get ? null
                : reads++ == failingRead ? Json(status, body) : Json(HttpStatusCode.OK, "[]"),
            ThrowFor = n => throws && n == failingRead + 1 ? new HttpRequestException("connection refused") : null,
        };
        for (var i = 0; i < 4; i++)
            Breaker.RecordFailure();

        await TreatmentSink(handler).OnUpdatedAsync(Edit("syn-3a7c0e9f1b2d4c6e"));

        handler.Methods.Should().HaveCount(failingRead + 1).And.OnlyContain(m => m == HttpMethod.Get);
        Breaker.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task TreatmentEdit_OfAConnectorTreatment_AsksAndSendsNothing()
    {
        var handler = new RecordingHttpMessageHandler();

        await TreatmentSink(handler).OnUpdatedAsync(
            new Treatment { Id = RecordUuid, LegacyId = "65a1b2c3d4e5f60718293a4b", EventType = "Note", DataSource = DataSources.NightscoutConnector });

        handler.RequestCount.Should().Be(0);
    }

    private static HttpResponseMessage DuplicateKey() => Json(
        HttpStatusCode.InternalServerError,
        """{"status":500,"message":"Mongo Error","description":"E11000 duplicate key error collection: nightscout.devicestatus index: _id_ dup key"}""");

    /// <summary>
    /// A status goes upstream under a stable <c>_id</c>, and Nightscout inserts statuses with an
    /// ordered <c>insertMany</c>, so one it already holds refuses the batch with a duplicate-key
    /// error and stops the statuses after it. The batch is sent again one status at a time; the one
    /// already there counts as written, and none of it counts against the shared circuit breaker.
    /// </summary>
    [Fact]
    public async Task DeviceStatusBatch_RefusedForAStatusAlreadyUpstream_IsSentOneByOneWithoutTrippingTheBreaker()
    {
        var handler = new RecordingHttpMessageHandler
        {
            RespondTo = (_, body) => body.StartsWith('[') && (JsonSerializer.Deserialize<JsonElement>(body).GetArrayLength() > 1
                    || body.Contains("\"loop_status_1\"", StringComparison.Ordinal))
                ? DuplicateKey()
                : null,
        };
        for (var i = 0; i < 4; i++)
            Breaker.RecordFailure();
        var sut = new NightscoutDeviceStatusWriteBackSink(
            new HttpClient(handler),
            CreateLoader(_config).Object,
            Breaker,
            NullLogger<NightscoutDeviceStatusWriteBackSink>.Instance);

        await sut.OnCreatedAsync(new[]
        {
            new DeviceStatus { Id = "loop_status_1", Device = "loop" },
            new DeviceStatus { Id = "loop_status_2", Device = "loop" },
            new DeviceStatus { Id = "loop_status_3", Device = "loop" },
        });

        handler.RequestCount.Should().Be(4);
        handler.Bodies.Skip(1).Select(b => JsonSerializer.Deserialize<JsonElement>(b)[0].GetProperty("identifier").GetString())
            .Should().Equal("loop_status_1", "loop_status_2", "loop_status_3");
        Breaker.IsOpen.Should().BeFalse();
        Breaker.RecordFailure();
        Breaker.IsOpen.Should().BeFalse("the refusal for a status already upstream reset the failure count");
    }

    [Fact]
    public async Task DeviceStatusBatch_RefusedForAnotherReason_CountsAsOneFailure()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError);
        for (var i = 0; i < 4; i++)
            Breaker.RecordFailure();
        var sut = new NightscoutDeviceStatusWriteBackSink(
            new HttpClient(handler),
            CreateLoader(_config).Object,
            Breaker,
            NullLogger<NightscoutDeviceStatusWriteBackSink>.Instance);

        await sut.OnCreatedAsync(new[] { new DeviceStatus { Id = "a", Device = "loop" }, new DeviceStatus { Id = "b", Device = "loop" } });

        handler.RequestCount.Should().Be(1);
        Breaker.IsOpen.Should().BeTrue();
    }

    private const string RecordUuid = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";

    private static Dictionary<string, string?> IdKeys(JsonElement body)
        => body.EnumerateObject()
            .Where(p => p.Name is "_id" or "id" or "Id" or "identifier" or "legacyId")
            .ToDictionary(p => p.Name, p => p.Value.GetString());
}
