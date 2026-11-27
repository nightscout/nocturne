using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A V4 sleep write answers a string longer than its column with a 400 that names the property,
/// whether the session comes alone, in a batch, or as an update.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class V4SleepColumnCapIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const int SourceAppColumnLength = 200;

    [Theory]
    [InlineData("POST", "api/v4/sleep/sessions", false)]
    [InlineData("POST", "api/v4/sleep/sessions/bulk", true)]
    [InlineData("PUT", "api/v4/sleep/sessions/{id}", false)]
    public async Task ASourceAppLongerThanItsColumn_IsRefused(string method, string route, bool isArray)
    {
        var session = new SleepSession { SourceApp = new string('a', SourceAppColumnLength + 1) };
        object body = isArray ? new[] { session } : session;
        using var client = CreateAuthenticatedClient();
        using var message = new HttpRequestMessage(new HttpMethod(method), "/" + route.Replace("{id}", Guid.NewGuid().ToString()))
        {
            Content = JsonContent.Create(body, body.GetType()),
        };

        using var response = await client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SourceApp").And.Contain($"{SourceAppColumnLength} characters");
    }
}
