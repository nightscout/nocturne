using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

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
    }

    [Fact]
    public async Task Sync_WhenGlookoRefusesTheSignIn_SendsTheTenantToTheirCredentials()
    {
        var result = await SyncAgainst(_ => Answer(HttpStatusCode.Unauthorized, "{}"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
    }

    private static async Task<SyncResult> SyncAgainst(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        using var authClient = new HttpClient(new FakeHttpHandler(answer));
        using var serviceClient = new HttpClient(new FakeHttpHandler(answer));
        var resolver = new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null);

        using var provider = new GlookoAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant.Accessor(),
            NullLogger<GlookoAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());
        var service = new GlookoConnectorService(
            serviceClient,
            resolver,
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            provider);

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new GlookoConnectorConfiguration { Email = "someone@example.com", Password = "hunter2" },
            CancellationToken.None);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

}
