using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Connectors.NocturneRemote.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

public class NocturneRemoteBackgroundSyncAuthTests
{
    private static NocturneRemoteConnectorService CreateService(
        HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var httpClient = new HttpClient(handler);
        if (timeout is { } budget)
            httpClient.Timeout = budget;
        return new NocturneRemoteConnectorService(
            httpClient,
            new ConnectorServerResolver<NocturneRemoteConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NocturneRemoteConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            publisher: null);
    }

    /// <summary>
    ///     Returns the right empty-collection JSON based on the request path so that both
    ///     paginated V4 endpoints (expect PaginatedResponse&lt;T&gt;) and array endpoints
    ///     (DeviceStatus v1, Food v4) can deserialize successfully.
    /// </summary>
    private static HttpMessageHandler RespondOkJson() =>
        new FuncHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            // v1 DeviceStatus and v4 Foods return a flat JSON array; everything else is paginated
            var isArrayEndpoint = path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/v4/foods", StringComparison.OrdinalIgnoreCase);

            var json = isArrayEndpoint
                ? "[]"
                : "{\"data\":[],\"pagination\":{\"total\":0,\"limit\":500,\"offset\":0}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

    [Fact]
    public async Task BackgroundSync_UsesTheTenantConfigUrl()
    {
        var tenantConfig = new NocturneRemoteConnectorConfiguration
        {
            Url = "https://remote.nocturne.example.com",
            AccessToken = "bearer-token",
            Enabled = true,
            SyncIntervalMinutes = 5,
        };

        var service = CreateService(RespondOkJson());

        var result = await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task BackgroundSync_WithEmptyTenantUrl_ReturnsFailure_NotException()
    {
        var tenantConfig = new NocturneRemoteConnectorConfiguration { Enabled = true, SyncIntervalMinutes = 5 };

        var service = CreateService(RespondOkJson());

        var act = async () => await service.SyncDataAsync(tenantConfig, CancellationToken.None, since: null);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Success.Should().BeFalse();
    }

    /// <summary>
    ///     The terminal progress message belongs to the shared run wrapper. A rejected token returns
    ///     before any data is fetched, and that path still owes the tenant exactly one terminal
    ///     message — without it the connector's badge stays on "syncing" until the page is reloaded.
    /// </summary>
    [Theory]
    [InlineData(true, SyncPhase.Completed)]
    [InlineData(false, SyncPhase.Failed)]
    public async Task RequestedSync_ReportsExactlyOneTerminalMessage(
        bool tokenAccepted, SyncPhase expectedPhase)
    {
        var handler = tokenAccepted
            ? RespondOkJson()
            : new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized")
            });
        var service = CreateService(handler);
        var reported = new List<SyncProgressEvent>();

        await service.SyncDataAsync(GlucoseSince(), RemoteConfig, CancellationToken.None, Recording(reported));

        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(expectedPhase);
    }

    /// <summary>
    ///     A remote that accepts the connection and then says nothing reaches the connector as a
    ///     client timeout, which is an <see cref="OperationCanceledException"/>. The shared run
    ///     wrapper reports any cancelled run with a terminal failure, but a source timeout is a
    ///     real failure with a reason worth naming, so the credential check ends the run with a
    ///     result of its own carrying "did not answer" instead.
    /// </summary>
    [Fact]
    public async Task RequestedSync_WhenTheRemoteStallsDuringTheCredentialCheck_FailsTheRunAndReportsATerminalMessage()
    {
        var service = CreateService(new StallingHandler(), timeout: TimeSpan.FromMilliseconds(200));
        var reported = new List<SyncProgressEvent>();

        var run = async () => await service.SyncDataAsync(
            GlucoseSince(), RemoteConfig, CancellationToken.None, Recording(reported));

        var result = (await run.Should().NotThrowAsync()).Subject;

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not answer")
            .And.NotContain("Authentication", "the credential is not what failed");
        result.Errors.Should().ContainSingle().Which.Should().Contain("did not answer");
        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
    }

    /// <summary>
    ///     A withdrawn run still releases the tenant's in-progress indicator: the cancellation
    ///     propagates, and the wrapper reports one terminal failure with no exception text.
    /// </summary>
    [Fact]
    public async Task RequestedSync_WhenTheCallerWithdrawsDuringTheCredentialCheck_PropagatesTheCancellation()
    {
        using var withdrawal = new CancellationTokenSource();
        var service = CreateService(new StallingHandler(withdrawal));
        var reported = new List<SyncProgressEvent>();

        var run = async () => await service.SyncDataAsync(
            GlucoseSince(), RemoteConfig, withdrawal.Token, Recording(reported));

        await run.Should().ThrowAsync<OperationCanceledException>();
        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(SyncPhase.Failed);
        reported.Single(e => e.Phase == SyncPhase.Failed).ErrorMessage.Should().BeNull();
    }

    private static NocturneRemoteConnectorConfiguration RemoteConfig => new()
    {
        Url = "https://remote.nocturne.example.com",
        AccessToken = "bearer-token",
    };

    private static SyncRequest GlucoseSince() =>
        new() { DataTypes = [SyncDataType.Glucose], From = DateTime.UtcNow.AddHours(-1) };

    private static ISyncProgressReporter Recording(List<SyncProgressEvent> into)
    {
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => into.Add(e))
            .Returns(Task.CompletedTask);
        return reporter.Object;
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    /// <summary>
    ///     A remote that accepts the connection and then never answers, ending only when the client's
    ///     timeout or <paramref name="withdrawTheRun"/> cancels the request.
    /// </summary>
    private sealed class StallingHandler(CancellationTokenSource? withdrawTheRun = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            withdrawTheRun?.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
