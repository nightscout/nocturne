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
            Token = "bearer-token",
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
    ///     client timeout, which is an <see cref="OperationCanceledException"/> — the one exception
    ///     the shared run wrapper deliberately does not convert into a reported outcome, because a
    ///     withdrawn run has none to report. Letting it escape the credential check leaves the
    ///     tenant's connector badge on "syncing" until the page is reloaded, so the check ends the
    ///     run with a result of its own instead.
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
    ///     The distinction that must survive: a run the caller withdrew is genuinely cancelled, and
    ///     owes no terminal message — unlike every other ending, which does.
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
        reported.Should().NotContain(e => e.Phase == SyncPhase.Failed || e.Phase == SyncPhase.Completed);
    }

    private static NocturneRemoteConnectorConfiguration RemoteConfig => new()
    {
        Url = "https://remote.nocturne.example.com",
        Token = "bearer-token",
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
