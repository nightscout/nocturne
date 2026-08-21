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
///     MaxRetryAttempts of 0 has to buy one attempt here too instead of skipping acquisition.
/// </summary>
public class AuthTokenProviderBaseRetryTests
{
    [Fact]
    public async Task ExecuteWithRetryAsync_ZeroMaxRetries_AttemptsExactlyOnce()
    {
        using var provider = BuildProvider();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
            },
            maxRetries: 0);

        token.Should().BeNull();
        attempts.Should().Be(1, "0 is clamped to a single attempt");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetryableFailure_AttemptsUpToMaxRetries()
    {
        using var provider = BuildProvider();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
            },
            maxRetries: 3);

        token.Should().BeNull();
        attempts.Should().Be(3, "maxRetries counts attempts, not retries on top of a first try");
    }

    private static RetryTokenProvider BuildProvider() => new(
        new HttpClient(),
        new ConnectorTokenCache(),
        new ConnectorServerResolver<TestConnectorConfig>(null, null, null),
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
            int maxRetries)
            => ExecuteWithRetryAsync(
                operation,
                Mock.Of<IRetryDelayStrategy>(),
                maxRetries,
                "test token acquisition",
                CancellationToken.None);

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
