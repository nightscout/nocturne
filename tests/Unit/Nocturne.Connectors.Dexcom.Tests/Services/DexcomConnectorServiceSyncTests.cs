using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Dexcom.Tests.Services;

public class DexcomConnectorServiceSyncTests
{
    private const string GlucosePath = "ReadPublisherLatestGlucoseValues";

    /// <summary>
    /// A null in Share's array is one malformed record. The service reads every entry before the
    /// mapper does, so the null has to be dropped there or the run fails on every cycle.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_NullEntryInTheShareArray_PublishesItsSiblings()
    {
        var publisher = new Mock<IConnectorPublisher>();
        var glucose = new Mock<IGlucosePublisher>();
        IReadOnlyList<SensorGlucose>? published = null;
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose).Returns(glucose.Object);
        glucose
            .Setup(g => g.PublishSensorGlucoseAsync(
                It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, string, WriteOrigin, CancellationToken>(
                (records, _, _, _) => published = records.ToList())
            .ReturnsAsync(true);

        var service = Service(
            (_, _) => Task.FromResult(Json(
                """[null,{"WT":"Date(1781956800000)","ST":"Date(1781956800000)","DT":"Date(1781956800000+0000)","Value":120,"Trend":"Flat"}]""")),
            publisher.Object);

        var result = await service.SyncDataAsync(Request(), Config(), CancellationToken.None);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        published.Should().ContainSingle().Which.Mgdl.Should().Be(120);
    }

    /// <summary>
    /// A caller who cancels has to stop the request in flight, not wait out Share's answer and then
    /// see the run reported as failed.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_CallerCancelsDuringTheFetch_AbortsTheRequest()
    {
        using var caller = new CancellationTokenSource();
        var requestAborted = false;
        var service = Service(async (_, requestToken) =>
        {
            await caller.CancelAsync();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), requestToken);
            }
            catch (OperationCanceledException)
            {
                requestAborted = true;
                throw;
            }

            return Json("[]");
        });

        await FluentActions.Awaiting(() => service.SyncDataAsync(Request(), Config(), caller.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        requestAborted.Should().BeTrue("the caller's token has to reach the Share request");
    }

    private static SyncRequest Request() => new() { DataTypes = [SyncDataType.Glucose] };

    private static DexcomConnectorConfiguration Config() =>
        new() { Username = "someone@example.com", Password = "hunter2", Server = "US" };

    private static DexcomConnectorService Service(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> glucose,
        IConnectorPublisher? publisher = null)
    {
        var resolver = new ConnectorServerResolver<DexcomConnectorConfiguration>(
            new Dictionary<string, string> { ["US"] = DexcomConstants.Servers.Us },
            config => ((DexcomConnectorConfiguration)config).Server,
            null);

        return new DexcomConnectorService(
            new HttpClient(new GlucoseHandler(glucose)),
            resolver,
            NullLogger<DexcomConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new SignedInTokenProvider(resolver),
            publisher);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class GlucoseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> glucose)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri?.AbsolutePath.Contains(GlucosePath, StringComparison.OrdinalIgnoreCase) == true
                ? glucose(request, cancellationToken)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Stands in for a completed Share login.</summary>
    private sealed class SignedInTokenProvider(IConnectorServerResolver<DexcomConnectorConfiguration> resolver)
        : DexcomAuthTokenProvider(
            new HttpClient(),
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant(),
            NullLogger<DexcomAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>())
    {
        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            DexcomConnectorConfiguration config, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                ("session-1", DateTime.UtcNow.AddHours(1), null));

        private static ITenantAccessor ResolvedTenant()
        {
            var tenant = new Mock<ITenantAccessor>();
            tenant.Setup(t => t.IsResolved).Returns(true);
            tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());
            return tenant.Object;
        }
    }
}
