using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.Twiist.Tests.Services;

/// <summary>
///     What a Twiist sync that could not sign in tells the tenant. The token provider is the only
///     place that saw why, so its classification has to reach the result: a source that never
///     answered has no credential to fix.
/// </summary>
public class TwiistSignInFailureTests
{
    [Fact]
    public async Task Sync_WhenTwiistCannotBeReached_DoesNotBlameTheCredentials()
    {
        var result = await SyncAgainst(_ => throw new HttpRequestException("No such host is known"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
    }

    /// <summary>Cognito answers a wrong password or unknown user with 400 and the reason in <c>__type</c>.</summary>
    [Theory]
    [InlineData("NotAuthorizedException", "Incorrect username or password.")]
    [InlineData("UserNotFoundException", "User does not exist.")]
    public async Task Sync_WhenTwiistRefusesTheSignIn_SendsTheTenantToTheirCredentials(string type, string message)
    {
        var result = await SyncAgainst(_ => Answer(
            HttpStatusCode.BadRequest, $"{{\"__type\":\"{type}\",\"message\":\"{message}\"}}"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
    }

    private static async Task<SyncResult> SyncAgainst(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        using var authClient = new HttpClient(new FakeHttpHandler(answer));
        using var serviceClient = new HttpClient(new FakeHttpHandler(answer));
        var resolver = new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null);

        using var provider = new TwiistAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant.Accessor(),
            NullLogger<TwiistAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());
        var service = new TwiistConnectorService(
            serviceClient,
            resolver,
            NullLogger<TwiistConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            provider);

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new TwiistConnectorConfiguration { Username = "someone@example.com", Password = "hunter2" },
            CancellationToken.None);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

}
