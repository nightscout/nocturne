using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class DocumentCreatedAtRoundTripIntegrationTests : ApiIntegrationTestBase
{
    private const string UploadedValue = "1999-01-01T00:00:00.000Z";

    public DocumentCreatedAtRoundTripIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    [Fact]
    public async Task V1Treatment_UploadedCreatedAtExtra_IsEchoedBesideCreatedAt()
    {
        using var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var createdAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new Dictionary<string, object>
            {
                ["eventType"] = "Note",
                ["notes"] = "created-at-round-trip",
                ["created_at"] = createdAt,
                ["createdAt"] = UploadedValue,
            },
        });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        var echoed = JsonNode.Parse(await post.Content.ReadAsStringAsync())!.AsArray().Should().ContainSingle().Subject!;
        echoed["createdAt"]!.GetValue<string>().Should().Be(UploadedValue);
        echoed["created_at"]!.GetValue<string>().Should().Be(createdAt);
    }
}
