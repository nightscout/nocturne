using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// What every token connector tells the tenant when its sign-in fails, stated once against a
/// minimal connector. A connector's own tests keep only how its source's answers map onto these
/// outcomes.
/// </summary>
public class TokenConnectorServiceSignInTests
{
    private const string LoginUrl = "https://source.example/login";

    [Fact]
    public async Task Sync_SourceCannotBeReached_RetriesAndDoesNotBlameTheCredentials()
    {
        var handler = new FakeHttpHandler().On(LoginUrl, _ => throw new HttpRequestException("No such host is known"));
        var service = new SignInProbeService(handler);

        var result = await service.RunAsync(maxRetryAttempts: 3);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
        handler.CallsTo(LoginUrl).Should().Be(3);
        service.SyncRan.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Sync_SourceRefusesTheCredentials_SendsTheTenantToThemAndSignsInOnce(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler().On(LoginUrl, FakeHttpHandler.Status(status));
        var service = new SignInProbeService(handler);

        var result = await service.RunAsync(maxRetryAttempts: 3);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
        handler.CallsTo(LoginUrl).Should().Be(1, "a refusal is the source's verdict, so it is not sent again");
        service.SyncRan.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Sync_SourceAnswersSomethingUnusable_SaysNothingAboutTheCredentials(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler().On(LoginUrl, FakeHttpHandler.Status(status));
        var service = new SignInProbeService(handler);

        var result = await service.RunAsync(maxRetryAttempts: 3);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary").And.NotContain("password");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
        service.SyncRan.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public async Task Sync_SourceKeepsFailing_SpendsExactlyTheConfiguredAttempts(int maxRetryAttempts, int logins)
    {
        var handler = new FakeHttpHandler().On(LoginUrl, FakeHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var service = new SignInProbeService(handler);

        var result = await service.RunAsync(maxRetryAttempts);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary");
        handler.CallsTo(LoginUrl).Should().Be(logins);
    }

    [Fact]
    public async Task Sync_SourceRecoversOnASecondAttempt_RunsTheSyncWithTheToken()
    {
        var handler = new FakeHttpHandler().On(
            LoginUrl,
            _ => throw new HttpRequestException("connection reset"),
            _ => FakeHttpHandler.Json("{}"));
        var service = new SignInProbeService(handler);

        var result = await service.RunAsync(maxRetryAttempts: 3);

        result.Success.Should().BeTrue();
        service.TokenSeen.Should().Be("token-1");
        handler.CallsTo(LoginUrl).Should().Be(2);
    }

    [Fact]
    public async Task Sync_SignInTimesOut_IsAFailedRunNotACancelledOne()
    {
        var handler = new FakeHttpHandler().On(LoginUrl, _ => throw new TaskCanceledException("The request timed out."));
        var service = new SignInProbeService(handler);

        var result = await service.RunAsync(maxRetryAttempts: 1);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary");
        service.SyncRan.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_CallerCancelsDuringTheSignIn_PropagatesTheCancellation()
    {
        using var caller = new CancellationTokenSource();
        var handler = new FakeHttpHandler().OnAsync(LoginUrl, async (_, token) =>
        {
            await caller.CancelAsync();
            token.ThrowIfCancellationRequested();
            return FakeHttpHandler.Json("{}");
        });
        var service = new SignInProbeService(handler);

        var run = () => service.RunAsync(maxRetryAttempts: 3, caller.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        handler.CallsTo(LoginUrl).Should().Be(1, "a withdrawn run is not retried");
        service.SyncRan.Should().BeFalse();
    }

    private sealed class SignInProbeService(FakeHttpHandler handler)
        : TokenConnectorService<TestConnectorConfig, HttpSignInProvider>(
            new HttpClient(handler),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            NullLogger<SignInProbeService>.Instance,
            new RecordingRetryDelayStrategy(),
            new HttpSignInProvider(new HttpClient(handler)))
    {
        public bool SyncRan { get; private set; }
        public string? TokenSeen { get; private set; }

        protected override string ConnectorSource => "probe";
        public override string ServiceName => "Probe";

        public Task<SyncResult> RunAsync(int maxRetryAttempts, CancellationToken cancellationToken = default) =>
            SyncDataAsync(
                new SyncRequest(),
                new TestConnectorConfig { MaxRetryAttempts = maxRetryAttempts },
                cancellationToken);

        protected override Task<SyncResult> SyncWithTokenAsync(
            SyncRequest request, TestConnectorConfig config, TokenSession session, CancellationToken cancellationToken)
        {
            SyncRan = true;
            TokenSeen = session.Token;
            return Task.FromResult(new SyncResult { Success = true });
        }
    }

    private sealed class HttpSignInProvider(HttpClient httpClient)
        : AuthTokenProviderBase<TestConnectorConfig>(
            httpClient,
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            ResolvedTenant.Accessor(),
            NullLogger<HttpSignInProvider>.Instance,
            new RecordingRetryDelayStrategy())
    {
        protected override string ConnectorName => "HttpSignIn";

        protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
        {
            var token = await ExecuteWithRetryAsync<string>(
                async _ =>
                {
                    using var response = await _httpClient.PostAsync(LoginUrl, null, cancellationToken);
                    if (response.IsSuccessStatusCode)
                        return ("token-1", false);

                    return (null, await HandleErrorResponseAsync(response, "sign-in", cancellationToken));
                },
                LoginAttempts(config),
                "sign-in",
                cancellationToken);

            return (token, DateTime.UtcNow.AddHours(1), null);
        }
    }
}
