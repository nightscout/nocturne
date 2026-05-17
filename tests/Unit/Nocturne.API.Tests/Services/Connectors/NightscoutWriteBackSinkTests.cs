using System.Net;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Constants;

namespace Nocturne.API.Tests.Services.Connectors;

[Trait("Category", "Unit")]
public class NightscoutWriteBackSinkTests
{
    private readonly NightscoutConnectorConfiguration _config;
    private readonly NightscoutCircuitBreaker _circuitBreaker;
    private readonly MockHttpMessageHandler _handler;
    private readonly NightscoutEntryWriteBackSink _sut;

    public NightscoutWriteBackSinkTests()
    {
        _config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret-12345",
            WriteBackEnabled = true,
            WriteBackBatchSize = 50
        };

        _circuitBreaker = new NightscoutCircuitBreaker();

        _handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(_config.Url)
        };

        _sut = new NightscoutEntryWriteBackSink(
            httpClient,
            CreateLoader(_config),
            _circuitBreaker,
            Mock.Of<ILogger<NightscoutEntryWriteBackSink>>());
    }

    private static IConnectorConfigurationLoader<NightscoutConnectorConfiguration> CreateLoader(
        NightscoutConnectorConfiguration config)
    {
        var loader = new Mock<IConnectorConfigurationLoader<NightscoutConnectorConfiguration>>();
        loader.Setup(l => l.LoadForTenantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        return loader.Object;
    }

    [Fact]
    public async Task OnCreatedAsync_PostsEntriesToNightscout()
    {
        var entries = new List<Entry>
        {
            new() { Id = "1", Sgv = 120, DataSource = "nocturne" },
            new() { Id = "2", Sgv = 130, DataSource = "nocturne" }
        };

        await _sut.OnCreatedAsync(entries);

        _handler.Requests.Should().HaveCount(1);
        var request = _handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.PathAndQuery.Should().Be("/api/v1/entries");
        request.Headers.GetValues("api-secret").Should().ContainSingle();
    }

    [Fact]
    public async Task OnCreatedAsync_SkipsEntriesFromNightscoutConnector()
    {
        var entries = new List<Entry>
        {
            new() { Id = "1", Sgv = 120, DataSource = DataSources.NightscoutConnector },
            new() { Id = "2", Sgv = 130, DataSource = DataSources.NightscoutConnector }
        };

        await _sut.OnCreatedAsync(entries);

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task OnCreatedAsync_FiltersOutNightscoutConnectorEntries()
    {
        var entries = new List<Entry>
        {
            new() { Id = "1", Sgv = 120, DataSource = "nocturne" },
            new() { Id = "2", Sgv = 130, DataSource = DataSources.NightscoutConnector }
        };

        await _sut.OnCreatedAsync(entries);

        _handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task OnCreatedAsync_SkipsAllWhenWriteBackDisabled()
    {
        _config.WriteBackEnabled = false;

        var entries = new List<Entry>
        {
            new() { Id = "1", Sgv = 120 }
        };

        await _sut.OnCreatedAsync(entries);

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task OnCreatedAsync_SkipsWhenCircuitBreakerIsOpen()
    {
        // Trip the circuit breaker
        for (var i = 0; i < 5; i++)
            _circuitBreaker.RecordFailure();

        _circuitBreaker.IsOpen.Should().BeTrue();

        var entries = new List<Entry>
        {
            new() { Id = "1", Sgv = 120 }
        };

        await _sut.OnCreatedAsync(entries);

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void CircuitBreaker_OpensAfterThresholdFailures()
    {
        _circuitBreaker.IsOpen.Should().BeFalse();

        for (var i = 0; i < 4; i++)
        {
            _circuitBreaker.RecordFailure();
            _circuitBreaker.IsOpen.Should().BeFalse();
        }

        _circuitBreaker.RecordFailure();
        _circuitBreaker.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void CircuitBreaker_ResetsOnSuccess()
    {
        for (var i = 0; i < 5; i++)
            _circuitBreaker.RecordFailure();

        _circuitBreaker.IsOpen.Should().BeTrue();

        _circuitBreaker.RecordSuccess();
        _circuitBreaker.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task OnCreatedAsync_RecordsFailureOnHttpError()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_config.Url)
        };
        var sink = new NightscoutEntryWriteBackSink(
            httpClient,
            CreateLoader(_config),
            _circuitBreaker,
            Mock.Of<ILogger<NightscoutEntryWriteBackSink>>());

        var entries = new List<Entry> { new() { Id = "1", Sgv = 120 } };

        // Should not throw (failures are non-fatal)
        await sink.OnCreatedAsync(entries);

        // But should record the failure
        // After 5 failures the breaker should open
        for (var i = 0; i < 4; i++)
            await sink.OnCreatedAsync(entries);

        _circuitBreaker.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task OnCreatedAsync_BatchesLargePayloads()
    {
        _config.WriteBackBatchSize = 2;

        var entries = new List<Entry>
        {
            new() { Id = "1", Sgv = 100 },
            new() { Id = "2", Sgv = 110 },
            new() { Id = "3", Sgv = 120 },
            new() { Id = "4", Sgv = 130 },
            new() { Id = "5", Sgv = 140 }
        };

        await _sut.OnCreatedAsync(entries);

        // 5 items with batch size 2 = 3 requests (2, 2, 1)
        _handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task OnUpdatedAsync_PutsEntryToNightscout()
    {
        var entry = new Entry { Id = "1", Sgv = 120, DataSource = "nocturne" };

        await _sut.OnUpdatedAsync(entry);

        _handler.Requests.Should().HaveCount(1);
        var request = _handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Put);
        request.RequestUri!.PathAndQuery.Should().Be("/api/v1/entries");
    }

    [Fact]
    public async Task OnUpdatedAsync_SkipsNightscoutConnectorEntry()
    {
        var entry = new Entry { Id = "1", Sgv = 120, DataSource = DataSources.NightscoutConnector };

        await _sut.OnUpdatedAsync(entry);

        _handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Test helper that captures HTTP requests for verification.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public List<HttpRequestMessage> Requests { get; } = [];

        public MockHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
