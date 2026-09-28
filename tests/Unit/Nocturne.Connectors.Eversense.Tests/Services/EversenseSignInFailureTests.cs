using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Eversense.Tests.Services;

/// <summary>
///     What a Eversense sync that could not sign in tells the tenant. The token provider is the only
///     place that saw why, so its classification has to reach the result: a source that never
///     answered has no credential to fix.
/// </summary>
public class EversenseSignInFailureTests
{
    [Fact]
    public async Task Sync_WhenEversenseCannotBeReached_DoesNotBlameTheCredentials()
    {
        var result = await SyncAgainst(_ => throw new HttpRequestException("No such host is known"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    /// <summary>Eversense's OAuth token endpoint answers a wrong password with 400 invalid_grant, never 401.</summary>
    [Fact]
    public async Task Sync_WhenEversenseRefusesTheSignIn_SendsTheTenantToTheirCredentials()
    {
        var result = await SyncAgainst(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_grant\",\"error_description\":\"Invalid username or password\"}")
        });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    private static async Task<SyncResult> SyncAgainst(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        using var authClient = new HttpClient(new StubHandler(answer));
        using var serviceClient = new HttpClient(new StubHandler(answer));
        var resolver = new ConnectorServerResolver<EversenseConnectorConfiguration>(null, null, null);

        using var provider = new EversenseAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant(),
            NullLogger<EversenseAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());
        var service = new EversenseConnectorService(
            serviceClient,
            resolver,
            NullLogger<EversenseConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            provider);

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new EversenseConnectorConfiguration { Username = "someone@example.com", Password = "hunter2", Server = "US" },
            CancellationToken.None);
    }

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
