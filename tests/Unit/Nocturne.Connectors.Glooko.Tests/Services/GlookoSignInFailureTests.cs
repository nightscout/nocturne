using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
///     What a Glooko sync that could not sign in tells the tenant. The token provider is the only
///     place that saw why, so its classification has to reach the result: a source that never
///     answered has no credential to fix.
/// </summary>
public class GlookoSignInFailureTests
{
    [Fact]
    public async Task Sync_WhenGlookoCannotBeReached_DoesNotBlameTheCredentials()
    {
        var result = await SyncAgainst(_ => throw new HttpRequestException("No such host is known"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task Sync_WhenGlookoRefusesTheSignIn_SendsTheTenantToTheirCredentials()
    {
        var result = await SyncAgainst(_ => Answer(HttpStatusCode.Unauthorized, "{}"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    private static async Task<SyncResult> SyncAgainst(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        using var authClient = new HttpClient(new StubHandler(answer));
        using var serviceClient = new HttpClient(new StubHandler(answer));
        var resolver = new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null);

        using var provider = new GlookoAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant(),
            NullLogger<GlookoAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());
        var service = new GlookoConnectorService(
            serviceClient,
            resolver,
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            provider);

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new GlookoConnectorConfiguration { Email = "someone@example.com", Password = "hunter2" },
            CancellationToken.None);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static ITenantAccessor ResolvedTenant()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.TenantId).Returns(Guid.NewGuid());
        return tenant.Object;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}
