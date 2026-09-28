using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Services;

/// <summary>
///     What a LibreLinkUp sync that could not sign in tells the tenant. The token provider is the only
///     place that saw why, so its classification has to reach the result: a source that never
///     answered has no credential to fix.
/// </summary>
public class LibreLinkUpSignInFailureTests
{
    [Fact]
    public async Task Sync_WhenLibreLinkUpCannotBeReached_DoesNotBlameTheCredentials()
    {
        var result = await SyncAgainst(_ => throw new HttpRequestException("No such host is known"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task Sync_WhenLibreLinkUpRefusesTheSignIn_SendsTheTenantToTheirCredentials()
    {
        // LibreLinkUp answers a wrong password with 200 and the refusal in the body.
        var result = await SyncAgainst(_ => Answer(HttpStatusCode.OK, """{"status":2,"error":{"message":"notAuthenticated"}}"""));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task Sync_WhenLibreLinkUpWantsTheAccountToAcceptNewTerms_SendsTheTenantToTheirAccount()
    {
        // No retry clears this 200: only the account holder accepting the terms in the app does.
        var result = await SyncAgainst(_ => Answer(
            HttpStatusCode.OK, """{"status":4,"data":{"step":{"type":"tou","componentName":"AcceptDocument"}}}"""));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in")
            .And.NotContain("usually temporary");
        result.Errors.Should().ContainSingle().Which.Should().Be(result.Message);
    }

    [Fact]
    public async Task Sync_WhenLibreLinkUpRefusesTheSignIn_SendsTheTenantToTheirCredentialsWithAStatus()
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
        var resolver = new ConnectorServerResolver<LibreLinkUpConnectorConfiguration>(
            null, null, LibreLinkUpConstants.Endpoints.Eu);

        using var provider = new LibreLinkAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant(),
            NullLogger<LibreLinkAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());
        var service = new LibreConnectorService(
            serviceClient,
            resolver,
            NullLogger<LibreConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            provider);

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new LibreLinkUpConnectorConfiguration { Username = "someone@example.com", Password = "hunter2" },
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
