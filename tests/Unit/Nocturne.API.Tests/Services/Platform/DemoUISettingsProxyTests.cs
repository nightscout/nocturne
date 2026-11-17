using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// <see cref="DemoUISettingsProxy"/> reads the demo service once per TTL, and remembers a failed
/// read for its own shorter TTL.
/// </summary>
[Trait("Category", "Unit")]
public class DemoUISettingsProxyTests
{
    private const string DeviceId = "from-demo-service";

    private readonly FakeTimeProvider _time = new();
    private readonly DemoService _service = new();
    private readonly DemoUISettingsProxy _proxy;

    public DemoUISettingsProxyTests()
    {
        var demoMode = new Mock<IDemoModeService>();
        demoMode.SetupGet(d => d.IsEnabled).Returns(true);
        demoMode.SetupGet(d => d.IsConfigured).Returns(true);
        demoMode.SetupGet(d => d.ServiceUrl).Returns("http://demo-service");

        _proxy = new DemoUISettingsProxy(
            demoMode.Object,
            _service.Factory,
            _time,
            NullLogger<DemoUISettingsProxy>.Instance);
    }

    [Fact]
    public async Task ServesTheCachedAnswerWithinTheTtl()
    {
        await _proxy.GetAsync(CancellationToken.None);
        _time.Advance(DemoUISettingsProxy.SuccessTtl - TimeSpan.FromSeconds(1));

        var settings = await _proxy.GetAsync(CancellationToken.None);

        settings!.Devices!.ConnectedDevices.Should().ContainSingle().Which.Id.Should().Be(DeviceId);
        _service.Calls.Should().Be(1);
    }

    [Fact]
    public async Task HandsEachCallerItsOwnCopy()
    {
        var first = await _proxy.GetAsync(CancellationToken.None);
        first!.Devices!.ConnectedDevices.Clear();

        var second = await _proxy.GetAsync(CancellationToken.None);

        second!.Devices!.ConnectedDevices.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadsTheDemoServiceAgainOnceTheTtlHasPassed()
    {
        await _proxy.GetAsync(CancellationToken.None);
        _time.Advance(DemoUISettingsProxy.SuccessTtl);

        await _proxy.GetAsync(CancellationToken.None);

        _service.Calls.Should().Be(2);
    }

    [Fact]
    public async Task RemembersAFailedReadWithoutAskingAgain()
    {
        _service.Status = HttpStatusCode.ServiceUnavailable;
        (await _proxy.GetAsync(CancellationToken.None)).Should().BeNull();
        _service.Status = HttpStatusCode.OK;
        _time.Advance(DemoUISettingsProxy.FailureTtl - TimeSpan.FromSeconds(1));

        var settings = await _proxy.GetAsync(CancellationToken.None);

        settings.Should().BeNull();
        _service.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AsksAgainOnceAFailedReadHasExpired()
    {
        _service.Status = HttpStatusCode.ServiceUnavailable;
        await _proxy.GetAsync(CancellationToken.None);
        _service.Status = HttpStatusCode.OK;
        _time.Advance(DemoUISettingsProxy.FailureTtl);

        var settings = await _proxy.GetAsync(CancellationToken.None);

        settings.Should().NotBeNull();
        _service.Calls.Should().Be(2);
    }

    [Fact]
    public async Task WaitsOnAHungDemoServiceOncePerFailureTtl()
    {
        _service.Hangs = true;
        var waiting = Task.WhenAll(_proxy.GetAsync(CancellationToken.None), _proxy.GetAsync(CancellationToken.None));
        await _service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(DemoUISettingsProxy.ProxyTimeout);

        var answers = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        var later = await _proxy.GetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        answers.Should().AllSatisfy(a => a.Should().BeNull());
        later.Should().BeNull();
        _service.Calls.Should().Be(1);
    }

    /// <summary>The external demo data service, counting the reads it answers.</summary>
    private sealed class DemoService
    {
        private int _calls;

        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        internal bool Hangs { get; set; }

        internal int Calls => _calls;

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IHttpClientFactory Factory
        {
            get
            {
                var factory = new Mock<IHttpClientFactory>();
                factory
                    .Setup(f => f.CreateClient(It.IsAny<string>()))
                    .Returns(() => new HttpClient(new Handler(this)) { Timeout = Timeout.InfiniteTimeSpan });

                return factory.Object;
            }
        }

        private sealed class Handler(DemoService owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                owner.Started.TrySetResult();

                if (owner.Hangs)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                return new HttpResponseMessage(owner.Status)
                {
                    Content = JsonContent.Create(new UISettingsConfiguration
                    {
                        Devices = new DeviceSettings { ConnectedDevices = [new ConnectedDevice { Id = DeviceId }] },
                    }),
                };
            }
        }
    }
}
