using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Regression tests: calling GetValidTokenAsync without a resolved tenant
///     must throw InvalidOperationException, not silently use Guid.Empty as the
///     cache key (which would leak tokens across tenants).
/// </summary>
public class AuthTokenProviderBaseTenantGuardTests
{
    private static readonly ConnectorServerResolver<TestConnectorConfig> NoOpResolver = new(null, null, null);

    [Fact]
    public async Task GetValidTokenAsync_Throws_WhenTenantNotResolved()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(false);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.Empty);

        using var httpClient = new HttpClient();
        var provider = new TestTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            NoOpResolver,
            tenantAccessor.Object,
            NullLogger<TestTokenProvider>.Instance);

        var config = new TestConnectorConfig();

        var act = () => provider.GetValidTokenAsync(config, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tenant*");
    }

    [Fact]
    public async Task GetValidTokenAsync_Succeeds_WhenTenantResolved()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        using var httpClient = new HttpClient();
        var provider = new TestTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            NoOpResolver,
            tenantAccessor.Object,
            NullLogger<TestTokenProvider>.Instance);

        var config = new TestConnectorConfig();

        var token = await provider.GetValidTokenAsync(config, CancellationToken.None);

        token.Should().Be("test-token");
    }
}

public class TestConnectorConfig : BaseConnectorConfiguration
{
    public TestConnectorConfig() => ConnectSource = ConnectSource.Dexcom;
}

file class TestTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<TestConnectorConfig> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger logger)
    : AuthTokenProviderBase<TestConnectorConfig>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    protected override string ConnectorName => "Test";

    protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        TestConnectorConfig config, CancellationToken cancellationToken)
    {
        return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
            ("test-token", DateTime.UtcNow.AddHours(1), null));
    }
}
