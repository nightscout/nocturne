using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// The sign-in step every token connector runs before it fetches, and the re-authentication it hands
/// its retry loop on a 401.
/// </summary>
public class TokenConnectorServiceTests
{
    [Fact]
    public async Task ValidToken_SignInRefused_RecordsTheProvidersReasonForTheFailedResult()
    {
        var provider = new ScriptedTokenProvider(refuse: true, tokens: [null]);
        var service = new ProbeService(provider);

        var token = await service.ValidToken();
        var result = service.Failed();

        token.Should().BeNull();
        provider.SignInFailureReason.Should().Contain("did not accept this sign-in");
        result.Success.Should().BeFalse();
        result.Message.Should().Be(provider.SignInFailureReason);
        result.Errors.Should().ContainSingle().Which.Should().Be(provider.SignInFailureReason);
    }

    [Fact]
    public async Task ValidToken_EmptyToken_IsAFailedSignIn()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: [""]);
        var service = new ProbeService(provider);

        var token = await service.ValidToken();

        token.Should().BeNull();
        service.Failed().Message.Should().Be(provider.SignInFailureReason);
    }

    [Fact]
    public async Task ValidToken_SignInSucceeds_PassesTheTokenThroughAndRecordsNothing()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["token-1"]);
        var service = new ProbeService(provider);

        var token = await service.ValidToken();

        token.Should().Be("token-1");
        service.Failed().Message.Should().Be("Authentication failed", "no sign-in failure was recorded");
    }

    [Fact]
    public async Task ReauthenticateWith_DropsTheRefusedTokenAndHandsTheCallerANewOne()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["old-token", "new-token"]);
        var service = new ProbeService(provider);
        var current = await service.ValidToken();

        var reauthenticated = await service.Reauthenticate(t => current = t)();

        reauthenticated.Should().BeTrue();
        current.Should().Be("new-token");
        provider.Acquired.Should().Be(2, "the cached token the source refused must not be served again");
    }

    [Fact]
    public async Task ReauthenticateWith_SignInFails_KeepsTheOldTokenAndReportsFailure()
    {
        var provider = new ScriptedTokenProvider(refuse: false, tokens: ["old-token", null]);
        var service = new ProbeService(provider);
        var current = await service.ValidToken();

        var reauthenticated = await service.Reauthenticate(t => current = t)();

        reauthenticated.Should().BeFalse();
        current.Should().Be("old-token");
        provider.Acquired.Should().Be(2);
    }

    /// <summary>Exposes the protected steps; never runs a sync.</summary>
    private sealed class ProbeService(ScriptedTokenProvider provider)
        : TokenConnectorService<TestConnectorConfig, ScriptedTokenProvider>(
            new HttpClient(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            NullLogger<ProbeService>.Instance,
            new RecordingRetryDelayStrategy(),
            provider)
    {
        private readonly TestConnectorConfig _config = new();

        protected override string ConnectorSource => "probe";
        public override string ServiceName => "Probe";

        public Task<string?> ValidToken() => ValidTokenAsync(_config, CancellationToken.None);

        public SyncResult Failed() => AuthenticationFailedResult();

        public Func<Task<bool>> Reauthenticate(Action<string> useToken) =>
            ReauthenticateWith(_config, useToken, CancellationToken.None);

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConnectorConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>Hands out <paramref name="tokens"/> in order, one per acquisition.</summary>
    private sealed class ScriptedTokenProvider(bool refuse, string?[] tokens)
        : AuthTokenProviderBase<TestConnectorConfig>(
            new HttpClient(),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            ResolvedTenant(),
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

        private static ITenantAccessor ResolvedTenant()
        {
            var accessor = new Mock<ITenantAccessor>();
            accessor.Setup(a => a.IsResolved).Returns(true);
            accessor.Setup(a => a.TenantId).Returns(Guid.NewGuid());
            return accessor.Object;
        }
    }
}
