using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for the atomic meal submission endpoint at
/// <c>POST /api/v4/nutrition/meals</c>.
/// </summary>
[Trait("Category", "Integration")]
[Parity]
public class MealsIntegrationTests : ApiIntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public MealsIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    [Fact]
    public async Task CreateMeal_NewRecord_Returns201WithCorrelationId()
    {
        using var client = CreateAuthenticatedClient();
        var request = new
        {
            timestamp = DateTimeOffset.UtcNow,
            insulin = 5.5,
            carbs = 45.0,
            dataSource = "nocturne-web",
        };

        var response = await client.PostAsJsonAsync("/api/v4/nutrition/meals", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CreateMealResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.CorrelationId.Should().NotBeEmpty();
        body.Bolus.CorrelationId.Should().Be(body.CorrelationId);
        body.CarbIntake.CorrelationId.Should().Be(body.CorrelationId);
    }

    [Fact]
    public async Task CreateMeal_FullRetryWithSameSyncIdentifier_Returns200()
    {
        using var client = CreateAuthenticatedClient();
        var request = new
        {
            timestamp = DateTimeOffset.UtcNow,
            insulin = 5.5,
            carbs = 45.0,
            dataSource = "aaps",
            syncIdentifier = Guid.NewGuid().ToString(),
        };

        var first = await client.PostAsJsonAsync("/api/v4/nutrition/meals", request);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/v4/nutrition/meals", request);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateMeal_DefaultTimestamp_Returns400()
    {
        using var client = CreateAuthenticatedClient();
        var request = new
        {
            // timestamp omitted (serializes as default)
            insulin = 5.0,
            carbs = 30.0,
        };

        var response = await client.PostAsJsonAsync("/api/v4/nutrition/meals", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
