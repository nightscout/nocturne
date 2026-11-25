using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Xunit;
using Nocturne.Tests.Shared.Connectors;

namespace Nocturne.Connectors.Dexcom.Tests.Services;

/// <summary>
///     What a Dexcom sync that could not sign in tells the tenant. The token provider is the only
///     place that saw why, so its classification has to reach the result: a source that never
///     answered has no credential to fix.
/// </summary>
public class DexcomSignInFailureTests
{
    [Fact]
    public async Task Sync_WhenDexcomCannotBeReached_DoesNotBlameTheCredentials()
    {
        var result = await SyncAgainst(_ => throw new HttpRequestException("No such host is known"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("usually temporary")
            .And.NotContain("password")
            .And.NotBe("Authentication failed");
    }

    [Fact]
    public async Task Sync_WhenDexcomRefusesTheSignIn_SendsTheTenantToTheirCredentials()
    {
        // Dexcom Share answers a wrong password with 500 and the refusal in the body's Code.
        var result = await SyncAgainst(_ => Answer(HttpStatusCode.InternalServerError, """{"Code":"AccountPasswordInvalid","Message":"Invalid password"}"""));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
    }

    [Fact]
    public async Task Sync_WhenDexcomRefusesTheSignIn_SendsTheTenantToTheirCredentialsWithAStatus()
    {
        var result = await SyncAgainst(_ => Answer(HttpStatusCode.Unauthorized, "{}"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not accept this sign-in");
    }

    private static async Task<SyncResult> SyncAgainst(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        using var authClient = new HttpClient(new FakeHttpHandler(answer));
        using var serviceClient = new HttpClient(new FakeHttpHandler(answer));
        var resolver = new ConnectorServerResolver<DexcomConnectorConfiguration>(
            new Dictionary<string, string> { ["US"] = DexcomConstants.Servers.Us },
            config => ((DexcomConnectorConfiguration)config).Server,
            null);

        using var provider = new DexcomAuthTokenProvider(
            authClient,
            new ConnectorTokenCache(),
            resolver,
            ResolvedTenant.Accessor(),
            NullLogger<DexcomAuthTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());
        var service = new DexcomConnectorService(
            serviceClient,
            resolver,
            NullLogger<DexcomConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            provider);

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] },
            new DexcomConnectorConfiguration { Username = "someone@example.com", Password = "hunter2", Server = "US" },
            CancellationToken.None);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

}
