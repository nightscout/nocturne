using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// The re-authentication a connector hands its retry loop on a 401. A failed sign-in must leave the
/// caller's token as it was, so the retry loop's "re-auth failed" branch is what decides the outcome
/// rather than a request sent with an empty credential.
/// </summary>
public class ReauthenticateWithTests
{
    [Fact]
    public async Task ReauthenticateWith_SignInFails_KeepsTheOldTokenAndReportsFailure()
    {
        var provider = new ScriptedTokenProvider(token: null);
        var current = "old-token";

        var reauthenticated = await ProbeService.Reauthenticate(provider, t => current = t)();

        reauthenticated.Should().BeFalse();
        current.Should().Be("old-token");
        provider.Acquired.Should().Be(1, "the refused token is dropped and a fresh sign-in attempted");
    }

    [Fact]
    public async Task ReauthenticateWith_SignInSucceeds_HandsTheNewTokenToTheCaller()
    {
        var provider = new ScriptedTokenProvider(token: "new-token");
        var current = "old-token";

        var reauthenticated = await ProbeService.Reauthenticate(provider, t => current = t)();

        reauthenticated.Should().BeTrue();
        current.Should().Be("new-token");
    }

    /// <summary>Exposes the protected factory; never runs a sync.</summary>
    private sealed class ProbeService()
        : BaseConnectorService<TestConnectorConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            NullLogger<ProbeService>.Instance)
    {
        protected override string ConnectorSource => "probe";
        public override string ServiceName => "Probe";

        public static Func<Task<bool>> Reauthenticate(
            AuthTokenProviderBase<TestConnectorConfig> provider, Action<string> useToken) =>
            ReauthenticateWith(provider, new TestConnectorConfig(), useToken, CancellationToken.None);

        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConnectorConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ScriptedTokenProvider(string? token)
        : AuthTokenProviderBase<TestConnectorConfig>(
            new HttpClient(),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
            ResolvedTenant(),
            NullLogger<ScriptedTokenProvider>.Instance)
    {
        public int Acquired { get; private set; }

        protected override string ConnectorName => "Scripted";

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
        {
            Acquired++;
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
