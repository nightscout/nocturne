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
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     counts total attempts, matching how the data paths in
///     <see cref="BaseConnectorService{TConfig}"/> read it, and is clamped to a floor of one so a
///     connector configured with 0 still authenticates.
/// </summary>
public class AuthTokenProviderBaseRetryTests
{
    private static readonly ConnectorServerResolver<TestConnectorConfig> NoOpResolver = new(null, null, null);

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
        var provider = new CountingTokenProvider(
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
}

/// <summary>Fails every login, recording how many times it was asked to try.</summary>
file class CountingTokenProvider(
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
