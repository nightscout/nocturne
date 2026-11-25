using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// The sign-in step every token connector runs before its sync, and the re-authentication the sync
/// hands its retry loop on a 401.
/// </summary>
public class TokenConnectorServiceTests
{
    [Fact]
    public async Task Sync_SignInRefused_FailsWithTheProvidersReasonAndNeverRunsTheSync()
    {
        var provider = new ScriptedTokenProvider(refuse: true, tokens: [null]);
        var service = new ProbeService(provider);

        var result = await service.RunAsync();

        provider.SignInFailureReason.Should().Contain("did not accept this sign-in");
        result.Success.Should().BeFalse();
        result.Message.Should().Be(provider.SignInFailureReason);
        result.Errors.Should().ContainSingle().Which.Should().Be(provider.SignInFailureReason);
        service.SyncRan.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_EmptyToken_IsAFailedSignInAndNeverRunsTheSync()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: [""]);
        var service = new ProbeService(provider);

        var result = await service.RunAsync();

        result.Success.Should().BeFalse();
        result.Message.Should().Be(provider.SignInFailureReason);
        service.SyncRan.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_SignInSucceeds_HandsTheSyncTheTokenAfterOneSignIn()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["token-1"]);
        var service = new ProbeService(provider);

        var result = await service.RunAsync();

        result.Success.Should().BeTrue();
        service.Failed().Message.Should().Be("Authentication failed");
        service.TokenSeen.Should().Be("token-1");
        provider.Acquired.Should().Be(1);
    }

    [Fact]
    public async Task Sync_PreparesBeforeTheSignIn()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["token-1"]);
        var service = new ProbeService(provider);

        await service.RunAsync();

        service.AcquiredWhenPrepared.Should().Be(0);
    }

    [Fact]
    public async Task Sync_NoSignInNeeded_SkipsPrepareAndSignInAndRunsTheSync()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: []);
        var service = new ProbeService(provider) { SignIn = false };

        var result = await service.RunAsync();

        result.Success.Should().BeTrue();
        service.SyncRan.Should().BeTrue();
        service.AcquiredWhenPrepared.Should().BeNull();
        provider.Acquired.Should().Be(0);
    }

    [Fact]
    public async Task Session_NoSignInNeeded_RefusesToYieldATokenOrSignInAgain()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: []);
        var service = new ProbeService(provider) { SignIn = false };

        await service.RunAsync();

        var token = () => service.Session!.Token;
        var reauthenticate = () => service.Session!.ReauthenticateAsync();
        token.Should().Throw<InvalidOperationException>();
        await reauthenticate.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Reauthenticate_DropsTheRefusedTokenAndHandsTheSyncANewOne()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["old-token", "new-token"]);
        var service = new ProbeService(provider);
        await service.RunAsync();

        var reauthenticated = await service.Session!.ReauthenticateAsync();

        reauthenticated.Should().BeTrue();
        service.Session.Token.Should().Be("new-token");
        provider.Acquired.Should().Be(2, "the cached token the source refused must not be served again");
    }

    [Fact]
    public async Task Reauthenticate_SignInFails_KeepsTheOldTokenAndRecordsTheReason()
    {
        var provider = new ScriptedTokenProvider(refuse: true, tokens: ["old-token", null]);
        var service = new ProbeService(provider);
        await service.RunAsync();

        var reauthenticated = await service.Session!.ReauthenticateAsync();

        reauthenticated.Should().BeFalse();
        service.Session.Token.Should().Be("old-token");
        provider.Acquired.Should().Be(2);
        service.Failed().Message.Should().Be(provider.SignInFailureReason);
    }

    [Fact]
    public async Task ScheduledSync_GlucosePointThatCannotBeRead_FailsBeforeThePrepareAndTheSignIn()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["token-1"]);
        var service = new ProbeService(provider, GlucoseReadThat(fails: true).Object) { ResumesFromGlucose = true };

        var result = await service.RunScheduledAsync();

        result.Success.Should().BeFalse();
        service.AcquiredWhenPrepared.Should().BeNull();
        provider.Acquired.Should().Be(0);
        service.SyncRan.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_NoSignInNeeded_ReadsNoResumePoint()
    {
        var publisher = GlucoseReadThat(fails: false);
        var service = new ProbeService(new ScriptedTokenProvider(refuse: false, tokens: []), publisher.Object)
        {
            SignIn = false,
            ResumesFromGlucose = true,
        };

        var result = await service.RunScheduledAsync();

        result.Success.Should().BeTrue();
        publisher.Verify(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IConnectorPublisher> GlucoseReadThat(bool fails)
    {
        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        var read = publisher.Setup(p => p.Glucose.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (fails)
            read.ThrowsAsync(new HttpRequestException("watermark store down"));
        else
            read.ReturnsAsync(DateTime.UtcNow);
        return publisher;
    }

    /// <summary>Runs the base's gate over a sync that records what it was handed.</summary>
    private sealed class ProbeService(ScriptedTokenProvider provider, IConnectorPublisher? publisher = null)
        : TokenConnectorService<TestConnectorConfig, ScriptedTokenProvider>(
            new HttpClient(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            NullLogger<ProbeService>.Instance,
            new RecordingRetryDelayStrategy(),
            provider,
            publisher)
    {
        private readonly TestConnectorConfig _config = new();

        public bool SignIn { get; init; } = true;
        public bool ResumesFromGlucose { get; init; }
        public bool SyncRan { get; private set; }
        public string? TokenSeen { get; private set; }
        public int? AcquiredWhenPrepared { get; private set; }
        public TokenSession? Session { get; private set; }

        protected override string ConnectorSource => "probe";
        public override string ServiceName => "Probe";

        public Task<SyncResult> RunAsync() => SyncDataAsync(new SyncRequest(), _config, CancellationToken.None);

        public Task<SyncResult> RunScheduledAsync() => SyncDataAsync(_config, CancellationToken.None);

        public SyncResult Failed() => AuthenticationFailedResult();

        protected override bool NeedsSignIn(SyncRequest request, TestConnectorConfig config) => SignIn;

        protected override IEnumerable<ResumeKey> ResumesFrom(SyncRequest request, TestConnectorConfig config) =>
            ResumesFromGlucose ? [ResumeFamily.Glucose] : [];

        protected override Task PrepareSignInAsync(TestConnectorConfig config, CancellationToken cancellationToken)
        {
            AcquiredWhenPrepared = provider.Acquired;
            return Task.CompletedTask;
        }

        protected override Task<SyncResult> SyncWithTokenAsync(
            SyncRequest request, TestConnectorConfig config, TokenSession session, CancellationToken cancellationToken)
        {
            SyncRan = true;
            Session = session;
            if (SignIn)
                TokenSeen = session.Token;
            return Task.FromResult(new SyncResult { Success = true });
        }
    }

    /// <summary>Hands out <paramref name="tokens"/> in order, one per acquisition.</summary>
    private sealed class ScriptedTokenProvider(bool refuse, string?[] tokens)
        : AuthTokenProviderBase<TestConnectorConfig>(
            new HttpClient(),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            ResolvedTenant.Accessor(),
            NullLogger<ScriptedTokenProvider>.Instance,
            new RecordingRetryDelayStrategy())
    {
        public int Acquired { get; private set; }

        protected override string ConnectorName => "Scripted";

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
        {
            var token = tokens[Acquired++];
            if (token is null && refuse)
                RecordSignInRefused();

            return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                (token, DateTime.UtcNow.AddHours(1), null));
        }

    }
}
