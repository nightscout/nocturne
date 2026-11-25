using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Connectors;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// The sign-in runs through the installed registration, so these tests see the resilience handler
/// production puts on the client. One failing sign-in must not be sent by both the handler and the
/// provider's own loop: the handler owns transport retries, and the tenant's
/// <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/> is the
/// most sends a run may make.
/// </summary>
public class GlookoSignInRetryOwnershipTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ASignInAnsweredUnavailableEveryTime_IsSentNoMoreThanMaxRetryAttempts(int maxRetryAttempts)
    {
        var glooko = new FakeGlooko(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var token = await SignInAsync(glooko, maxRetryAttempts);

        token.Should().BeNull();
        glooko.SignIns.Should().Be(maxRetryAttempts);
    }

    [Fact]
    public async Task ASignInThatCannotConnectEveryTime_IsSentNoMoreThanMaxRetryAttempts()
    {
        var glooko = new FakeGlooko(_ => throw new HttpRequestException("connection reset"));

        var token = await SignInAsync(glooko, maxRetryAttempts: 3);

        token.Should().BeNull();
        glooko.SignIns.Should().Be(3);
    }

    [Fact]
    public async Task ASignInTheHandlerRecoversFromInOneRun_ReturnsTheSession()
    {
        var glooko = new FakeGlooko(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => SignInSuccess());

        var token = await SignInAsync(glooko, maxRetryAttempts: 3);

        token.Should().Be($"{GlookoConstants.SessionCookieName}=session-abc");
        glooko.SignIns.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ASignInAnsweredWithARefusal_IsSentOnce_AndNamesTheCredentials(HttpStatusCode refusal)
    {
        var glooko = new FakeGlooko(_ => new HttpResponseMessage(refusal));

        var (token, reason) = await SignInWithReasonAsync(glooko, maxRetryAttempts: 3);

        token.Should().BeNull();
        glooko.SignIns.Should().Be(1);
        reason.Should().Contain("did not accept this sign-in");
    }

    [Fact]
    public async Task ASignInAnsweredUnavailableAfterTheBudget_SaysNothingAboutTheCredentials()
    {
        var glooko = new FakeGlooko(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var (_, reason) = await SignInWithReasonAsync(glooko, maxRetryAttempts: 3);

        reason.Should().Contain("usually temporary").And.NotContain("did not accept this sign-in");
    }

    /// <summary>
    /// A 200 whose profile carries no Glooko code is not a refusal a second sign-in could act on:
    /// the run ends there as unavailable, and the next sync tries again.
    /// </summary>
    [Fact]
    public async Task AV3SignInWhoseProfileCarriesNoCode_IsFinal()
    {
        var glooko = new FakeGlooko(request => request.Method == HttpMethod.Post
            ? SignInSuccess("{\"success\":true,\"two_fa_required\":false}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"currentUser\":{\"meterUnits\":\"mgdl\"}}"),
            });

        var (token, reason) = await SignInWithReasonAsync(glooko, maxRetryAttempts: 3, useV3Api: true);

        token.Should().BeNull();
        glooko.SignIns.Should().Be(1);
        glooko.Profiles.Should().Be(1);
        reason.Should().Contain("usually temporary");
    }

    /// <summary>
    /// The profile fetch is part of the sign-in, so it carries the same attempt budget and the
    /// provider does not sign in again after the handler has spent it.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AV3ProfileAnsweredUnavailableEveryTime_IsSentNoMoreThanMaxRetryAttempts(int maxRetryAttempts)
    {
        var glooko = new FakeGlooko(request => request.Method == HttpMethod.Post
            ? SignInSuccess("{\"success\":true,\"two_fa_required\":false}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var token = await SignInAsync(glooko, maxRetryAttempts, useV3Api: true);

        token.Should().BeNull();
        glooko.SignIns.Should().Be(1);
        glooko.Profiles.Should().Be(maxRetryAttempts);
    }

    private static async Task<string?> SignInAsync(
        FakeGlooko glooko, int maxRetryAttempts, bool useV3Api = false) =>
        (await SignInWithReasonAsync(glooko, maxRetryAttempts, useV3Api)).Token;

    private static async Task<(string? Token, string Reason)> SignInWithReasonAsync(
        FakeGlooko glooko, int maxRetryAttempts, bool useV3Api = false)
    {
        var time = new FakeTimeProvider();
        await using var provider = BuildProvider(glooko, time);
        await using var scope = provider.CreateAsyncScope();
        var tokenProvider = scope.ServiceProvider.GetRequiredService<GlookoAuthTokenProvider>();

        var work = tokenProvider.GetValidTokenAsync(
            new GlookoConnectorConfiguration
            {
                Email = "someone@example.com",
                Password = "hunter2",
                UseV3Api = useV3Api,
                MaxRetryAttempts = maxRetryAttempts,
            },
            CancellationToken.None);

        await Eventually.UntilAsync(
            "the sign-in to finish",
            () => work.IsCompleted,
            step: TimeSpan.FromMilliseconds(1),
            eachStep: () => time.Advance(TimeSpan.FromSeconds(5)));

        return (await work, tokenProvider.SignInFailureReason);
    }

    private static ServiceProvider BuildProvider(FakeGlooko glooko, FakeTimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(new Mock<IRetryDelayStrategy>().Object);
        services.AddSingleton(ResolvedTenant.Accessor());
        services.AddSingleton(new Mock<IConnectorConfigurationService>(MockBehavior.Strict).Object);

        new GlookoConnectorInstaller().Install(
            services,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Connectors:Glooko:Enabled"] = "true" })
                .Build());

        // The link-local guard resolves the real Glooko host names; the fake answers in its place.
        services.AddHttpClient(nameof(GlookoAuthTokenProvider))
            .ConfigurePrimaryHttpMessageHandler(() => glooko)
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
                handlers.Remove(handlers.Single(h => h is LinkLocalGuardHandler)));

        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage SignInSuccess(string body = "{\"userLogin\":{\"glookoCode\":\"blue-duke-4165\"}}")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.TryAddWithoutValidation(
            "Set-Cookie", $"{GlookoConstants.SessionCookieName}=session-abc; Path=/; HttpOnly");
        return response;
    }

    /// <summary>Answers each sign-in from the next step of a script and counts them.</summary>
    private sealed class FakeGlooko(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        private int _signIns;
        private int _profiles;

        public int SignIns => _signIns;

        public int Profiles => _profiles;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isSignIn = request.Method == HttpMethod.Post;
            if (!isSignIn)
                Interlocked.Increment(ref _profiles);
            var index = isSignIn ? Interlocked.Increment(ref _signIns) - 1 : 0;
            var step = steps[Math.Min(index, steps.Length - 1)];
            return Task.FromResult(step(request));
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
