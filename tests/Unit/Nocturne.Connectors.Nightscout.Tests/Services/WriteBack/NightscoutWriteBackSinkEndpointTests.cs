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
/// One pass over every concrete sink: the v1 endpoint it targets, whether it filters
/// connector-sourced records, and the blast radius of the shared circuit breaker.
/// A wrong endpoint or a missing skip rule shows up as duplicated or missing data in
/// the tenant's legacy instance while both systems are live.
/// </summary>
[Trait("Category", "Unit")]
public class NightscoutWriteBackSinkEndpointTests
{
    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = "https://nightscout.example.com",
        ApiSecret = "test-secret-12345",
        WriteBackEnabled = true,
        WriteBackBatchSize = 50
    };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly NightscoutCircuitBreaker _breaker;

    public NightscoutWriteBackSinkEndpointTests()
    {
        _breaker = new NightscoutCircuitBreaker(_time);
    }

    private static IConnectorConfigurationLoader<NightscoutConnectorConfiguration> LoaderFor(
        NightscoutConnectorConfiguration config)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        return loader.Object;
    }

    private HttpClient Client(RecordingHttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri(_config.Url) };

    private NightscoutEntryWriteBackSink EntrySink(RecordingHttpMessageHandler h, NightscoutConnectorConfiguration? c = null)
        => new(Client(h), LoaderFor(c ?? _config), _breaker, NullLogger<NightscoutEntryWriteBackSink>.Instance);

    private NightscoutTreatmentWriteBackSink TreatmentSink(RecordingHttpMessageHandler h)
        => new(Client(h), LoaderFor(_config), _breaker, NullLogger<NightscoutTreatmentWriteBackSink>.Instance);

    private NightscoutDeviceStatusWriteBackSink DeviceStatusSink(RecordingHttpMessageHandler h)
        => new(Client(h), LoaderFor(_config), _breaker, NullLogger<NightscoutDeviceStatusWriteBackSink>.Instance);

    private NightscoutProfileWriteBackSink ProfileSink(RecordingHttpMessageHandler h)
        => new(Client(h), LoaderFor(_config), _breaker, NullLogger<NightscoutProfileWriteBackSink>.Instance);

    private NightscoutFoodWriteBackSink FoodSink(RecordingHttpMessageHandler h)
        => new(Client(h), LoaderFor(_config), _breaker, NullLogger<NightscoutFoodWriteBackSink>.Instance);

    private NightscoutActivityWriteBackSink ActivitySink(RecordingHttpMessageHandler h)
        => new(Client(h), LoaderFor(_config), _breaker, NullLogger<NightscoutActivityWriteBackSink>.Instance);

    /// <summary>
    /// A tenant may store a bare host, and one whose name opens with the scheme's letters must
    /// still be read as a host: taken for an absolute URL it becomes a relative request and the
    /// write-back lands somewhere other than the tenant's instance.
    /// </summary>
    [Fact]
    public async Task EntrySink_SchemelessHostNamedLikeAScheme_PostsToThatHost()
    {
        var handler = new RecordingHttpMessageHandler();
        var config = new NightscoutConnectorConfiguration
        {
            Url = "httpbin.example.com",
            ApiSecret = "test-secret-12345",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50
        };

        await EntrySink(handler, config).OnCreatedAsync(
            new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        handler.Uris.Should().ContainSingle()
            .Which.Should().Be(new Uri("https://httpbin.example.com/api/v1/entries"));
    }

    [Fact]
    public async Task EntrySink_PostsToV1Entries()
    {
        var handler = new RecordingHttpMessageHandler();

        await EntrySink(handler).OnCreatedAsync(new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        handler.Uris.Should().ContainSingle().Which.AbsolutePath.Should().Be("/api/v1/entries");
    }

    [Fact]
    public async Task TreatmentSink_PostsToV1Treatments()
    {
        var handler = new RecordingHttpMessageHandler();

        await TreatmentSink(handler).OnCreatedAsync(
            new Treatment { Id = "1", EventType = "Correction Bolus", DataSource = "nocturne" });

        handler.Uris.Should().ContainSingle().Which.AbsolutePath.Should().Be("/api/v1/treatments");
    }

    [Fact]
    public async Task DeviceStatusSink_PostsToV1DeviceStatus()
    {
        var handler = new RecordingHttpMessageHandler();

        await DeviceStatusSink(handler).OnCreatedAsync(new DeviceStatus { Id = "1", Device = "pump" });

        handler.Uris.Should().ContainSingle().Which.AbsolutePath.Should().Be("/api/v1/devicestatus");
    }

    [Fact]
    public async Task ProfileSink_PostsToV1Profile()
    {
        var handler = new RecordingHttpMessageHandler();

        await ProfileSink(handler).OnCreatedAsync(new Profile { Id = "1", DefaultProfile = "Default" });

        handler.Uris.Should().ContainSingle().Which.AbsolutePath.Should().Be("/api/v1/profile");
    }

    [Fact]
    public async Task FoodSink_PostsToV1Food()
    {
        var handler = new RecordingHttpMessageHandler();

        await FoodSink(handler).OnCreatedAsync(new Food { Id = "1", Name = "Apple" });

        handler.Uris.Should().ContainSingle().Which.AbsolutePath.Should().Be("/api/v1/food");
    }

    [Fact]
    public async Task ActivitySink_PostsToV1Activity()
    {
        var handler = new RecordingHttpMessageHandler();

        await ActivitySink(handler).OnCreatedAsync(new Activity { Id = "1", Type = "Exercise" });

        handler.Uris.Should().ContainSingle().Which.AbsolutePath.Should().Be("/api/v1/activity");
    }

    [Fact]
    public async Task TreatmentSink_SkipsRecordsSourcedFromTheNightscoutConnector()
    {
        var handler = new RecordingHttpMessageHandler();

        await TreatmentSink(handler).OnCreatedAsync(new Treatment
        {
            Id = "1",
            EventType = "Correction Bolus",
            DataSource = DataSources.NightscoutConnector
        });

        handler.RequestCount.Should().Be(0);
    }

    /// <summary>
    /// Pins current behaviour: only entries and treatments carry a DataSource, so the
    /// remaining four sinks have no loop guard. Records the connector just pulled from
    /// the tenant's Nightscout are written straight back to it.
    /// </summary>
    [Fact]
    public async Task DeviceStatusProfileFoodAndActivitySinks_HaveNoLoopPreventionFilter()
    {
        var deviceStatus = new RecordingHttpMessageHandler();
        var profile = new RecordingHttpMessageHandler();
        var food = new RecordingHttpMessageHandler();
        var activity = new RecordingHttpMessageHandler();

        await DeviceStatusSink(deviceStatus).OnCreatedAsync(new DeviceStatus { Id = "1", Device = "pump" });
        await ProfileSink(profile).OnCreatedAsync(new Profile { Id = "1", DefaultProfile = "Default" });
        await FoodSink(food).OnCreatedAsync(new Food { Id = "1", Name = "Apple" });
        await ActivitySink(activity).OnCreatedAsync(new Activity { Id = "1", Type = "Exercise" });

        const string because =
            "this pins CURRENT behaviour, not desired behaviour: only Entry and Treatment "
            + "carry a DataSource, so these four sinks have no loop guard and echo "
            + "connector-pulled records straight back to the instance they came from. "
            + "Invert once these models can be attributed to a source";

        deviceStatus.RequestCount.Should().Be(1, because);
        profile.RequestCount.Should().Be(1, because);
        food.RequestCount.Should().Be(1, because);
        activity.RequestCount.Should().Be(1, because);
    }

    /// <summary>
    /// The breaker is a singleton shared by every sink, so failures writing one
    /// collection suspend write-back of all the others.
    /// </summary>
    [Fact]
    public async Task FailuresOnOneSink_SuspendWriteBackOnEveryOtherSink()
    {
        var failing = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError);
        var entrySink = EntrySink(failing);

        for (var i = 0; i < 5; i++)
            await entrySink.OnCreatedAsync(new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        var treatments = new RecordingHttpMessageHandler();
        var deviceStatus = new RecordingHttpMessageHandler();

        await TreatmentSink(treatments).OnCreatedAsync(
            new Treatment { Id = "1", EventType = "Correction Bolus", DataSource = "nocturne" });
        await DeviceStatusSink(deviceStatus).OnCreatedAsync(new DeviceStatus { Id = "1", Device = "pump" });

        _breaker.IsOpen.Should().BeTrue();
        treatments.RequestCount.Should().Be(0);
        deviceStatus.RequestCount.Should().Be(0);
    }

    /// <summary>
    /// Pins current behaviour: the breaker is registered as a process-wide singleton
    /// while the destination is per tenant, so one tenant's unreachable Nightscout
    /// stops write-back for every other tenant on the instance. The same singleton
    /// also gates <c>RequestForwardingService</c> (the v1 compatibility proxy), so the
    /// blast radius covers proxied legacy reads as well as write-back.
    /// </summary>
    [Fact]
    public async Task OneTenantsFailures_SuspendWriteBackForADifferentTenant()
    {
        var tenantA = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError);
        var sinkA = EntrySink(tenantA);

        for (var i = 0; i < 5; i++)
            await sinkA.OnCreatedAsync(new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" });

        var tenantBConfig = new NightscoutConnectorConfiguration
        {
            Url = "https://tenant-b.example.com",
            ApiSecret = "another-secret",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50
        };
        var tenantB = new RecordingHttpMessageHandler();

        await EntrySink(tenantB, tenantBConfig)
            .OnCreatedAsync(new Entry { Id = "2", Sgv = 130, DataSource = "nocturne" });

        tenantB.RequestCount.Should().Be(
            0,
            "this pins CURRENT behaviour, not desired behaviour: the breaker is a "
            + "process-wide singleton while the destination is per tenant, so one "
            + "tenant's dead Nightscout suspends write-back (and the v1 compatibility "
            + "proxy) for everyone. Invert once breaker state is keyed per tenant");
    }

    /// <summary>
    /// Integration seam: the API registers write-back behind the collection's own sink
    /// inside a <see cref="CompositeDataEventSink{T}"/> (ServiceRegistrationExtensions,
    /// the IDataEventSink&lt;Entry&gt; factory). A throwing sink is placed first so the
    /// composite's isolation actually has to catch something, and the spy sits after
    /// write-back so it only fires if the fan-out reaches past both.
    /// </summary>
    [Fact]
    public async Task CompositeSink_ReachesEverySinkPastAThrowingOne()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.ServiceUnavailable);
        var exploding = new ThrowingEntrySink();
        var spy = new SpyEntrySink();
        var composite = new CompositeDataEventSink<Entry>(
            [exploding, EntrySink(handler), spy],
            NullLogger<CompositeDataEventSink<Entry>>.Instance);

        var act = async () => await composite.OnCreatedAsync(
            new List<Entry> { new() { Id = "1", Sgv = 120, DataSource = "nocturne" } });

        await act.Should().NotThrowAsync();
        exploding.Invoked.Should().BeTrue();
        handler.RequestCount.Should().Be(1, "write-back must still run after a sink threw");
        spy.CreatedBatches.Should().Be(1, "sinks registered after write-back must still run");
    }

    private sealed class ThrowingEntrySink : IDataEventSink<Entry>
    {
        public bool Invoked { get; private set; }

        public Task OnCreatedAsync(IReadOnlyList<Entry> items, CancellationToken ct = default)
        {
            Invoked = true;
            throw new InvalidOperationException("sink failure");
        }
    }

    private sealed class SpyEntrySink : IDataEventSink<Entry>
    {
        public int CreatedBatches { get; private set; }

        public Task OnCreatedAsync(IReadOnlyList<Entry> items, CancellationToken ct = default)
        {
            CreatedBatches++;
            return Task.CompletedTask;
        }
    }
}
