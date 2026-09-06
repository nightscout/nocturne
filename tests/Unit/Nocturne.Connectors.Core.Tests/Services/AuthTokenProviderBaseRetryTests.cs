using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Token acquisition runs on the same retry loop as the connector services, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     counts total attempts here too, and 0 has to buy one attempt instead of skipping acquisition.
/// </summary>
public class AuthTokenProviderBaseRetryTests
{
    [Fact]
    public async Task ExecuteWithRetryAsync_ZeroMaxRetries_AttemptsExactlyOnce()
    {
        using var provider = BuildProvider();
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
            },
            delays,
            maxRetries: 0);

        token.Should().BeNull();
        attempts.Should().Be(1, "0 is clamped to a single attempt");
        delays.DelayedAttempts.Should().BeEmpty("a single attempt has nothing to wait between");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetryableFailure_AttemptsUpToMaxRetries()
    {
        using var provider = BuildProvider();
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
            },
            delays,
            maxRetries: 3);

        token.Should().BeNull();
        attempts.Should().Be(3, "maxRetries counts attempts, not retries on top of a first try");
        delays.DelayedAttempts.Should().Equal([0, 1], "three attempts leave two gaps to delay in");
    }

    /// <summary>
    ///     The configured value is what reaches the login loop, so a tenant raising or lowering
    ///     it changes how many times the connector authenticates.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    public async Task AcquireToken_AttemptsLoginAsManyTimesAsMaxRetryAttempts(
        int maxRetryAttempts, int expectedAttempts)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        using var httpClient = new HttpClient();
        using var provider = new CountingTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            NoOpResolver,
            tenantAccessor.Object,
            NullLogger<CountingTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());

        var token = await provider.GetValidTokenAsync(
            new TestConnectorConfig { MaxRetryAttempts = maxRetryAttempts },
            CancellationToken.None);

        token.Should().BeNull("every login attempt was made to fail");
        provider.LoginCalls.Should().Be(expectedAttempts);
    }

    private static readonly ConnectorServerResolver<TestConnectorConfig> NoOpResolver = new(null, null, null);

    private static RetryTokenProvider BuildProvider() => new(
        new HttpClient(),
        new ConnectorTokenCache(),
        NoOpResolver,
        Mock.Of<ITenantAccessor>(),
        NullLogger<RetryTokenProvider>.Instance);

    private sealed class RetryTokenProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TestConnectorConfig> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger logger)
        : AuthTokenProviderBase<TestConnectorConfig>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
    {
        protected override string ConnectorName => "Test";

        // Exposes the protected retry helper so its attempt-count behaviour can be tested directly.
        public Task<string?> InvokeExecuteWithRetryAsync(
            Func<int, Task<(string? Result, bool ShouldRetry)>> operation,
            IRetryDelayStrategy retryDelayStrategy,
            int maxRetries)
            => ExecuteWithRetryAsync(
                operation,
                retryDelayStrategy,
                maxRetries,
                "test token acquisition",
                CancellationToken.None);

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>Fails every login, recording how many times it was asked to try.</summary>
    private sealed class CountingTokenProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TestConnectorConfig> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy)
        : AuthTokenProviderBase<TestConnectorConfig>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
    {
        internal int LoginCalls { get; private set; }

        protected override string ConnectorName => "Counting";

        protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
        {
            var token = await ExecuteWithRetryAsync<string>(
                _ =>
                {
                    LoginCalls++;
                    return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
                },
                retryDelayStrategy,
                LoginAttempts(config),
                "counting login",
                cancellationToken);

            return (token, DateTime.UtcNow.AddHours(1), null);
        }
    }
}
