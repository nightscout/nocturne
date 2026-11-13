using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

public class NightscoutUnboundedFetchTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnchorUnboundedFetch_WithNoBounds_AnchorsTheUpperBoundAtTheClock()
    {
        NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>
            .AnchorUnboundedFetch(from: null, to: null, Now)
            .Should().Be(Now);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnchorUnboundedFetch_WithABound_PassesTheUpperBoundThrough(bool hasFrom, bool hasTo)
    {
        var to = hasTo ? Now.AddDays(-1) : (DateTime?)null;
        var from = hasFrom ? Now.AddDays(-3) : (DateTime?)null;

        NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>
            .AnchorUnboundedFetch(from, to, Now)
            .Should().Be(to);
    }

    [Fact]
    public async Task SyncDataAsync_WithAnOpenWindow_BoundsTheFirstGlucosePageAtTheServiceClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        var handler = new RecordingHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") },
        };
        var publisher = new Mock<IConnectorPublisher> { DefaultValue = DefaultValue.Mock };
        publisher.Setup(p => p.IsAvailable).Returns(true);
        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());
        var service = new NightscoutConnectorService(
            new HttpClient(handler),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            registration.Object,
            publisher.Object,
            clock);

        await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new NightscoutConnectorConfiguration { Url = "https://ns.example", ApiSecret = "secret" },
            CancellationToken.None);

        var nowMs = new DateTimeOffset(Now).ToUnixTimeMilliseconds();
        handler.Uris.Select(u => Uri.UnescapeDataString(u.PathAndQuery))
            .Should().Contain(u => u.Contains("/api/v1/entries.json") && u.Contains($"find[date][$lte]={nowMs}"));
    }
}
