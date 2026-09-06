using System.Net;
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
}
