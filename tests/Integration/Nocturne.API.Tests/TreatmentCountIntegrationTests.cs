using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The no-filter treatment count adds up every table the legacy projection serves, records, state
/// spans and profile activations alike, a Meal Bolus once, on real PostgreSQL, where the state-span
/// filters run.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class TreatmentCountIntegrationTests : ApiIntegrationTestBase
{
    public TreatmentCountIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string MinutesAgo(int minutes) => Now.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private async Task SeedAsync(HttpClient client)
    {
        object[] treatments =
        [
            new { _id = "6a1f00000000000000000001", eventType = "Meal Bolus", insulin = 2.0, carbs = 30, created_at = MinutesAgo(30) },
            new { _id = "6a1f00000000000000000002", eventType = "Correction Bolus", insulin = 1.0, notes = "with the bolus", created_at = MinutesAgo(40) },
            new { _id = "6a1f00000000000000000003", eventType = "Carb Correction", carbs = 15, created_at = MinutesAgo(300) },
            new
            {
                _id = "6a1f00000000000000000004", eventType = "Temporary Override", created_at = MinutesAgo(300),
                enteredBy = "Loop", reason = "Running", duration = 60, insulinNeedsScaleFactor = 0.8,
            },
            new
            {
                _id = "6a1f00000000000000000005", eventType = "Temporary Target", created_at = MinutesAgo(50),
                enteredBy = "AndroidAPS", reason = "Activity", duration = 45, targetTop = 8.0, targetBottom = 8.0,
                units = "mmol",
            },
            new
            {
                _id = "6a1f00000000000000000006", eventType = "Profile Switch", created_at = MinutesAgo(60),
                enteredBy = "AndroidAPS", profile = "Weekend", duration = 0,
            },
            new { _id = "6a1f00000000000000000007", eventType = "Note", notes = "apart", created_at = MinutesAgo(300) },
        ];

        var response = await client.PostAsJsonAsync("/api/v1/treatments", treatments);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<long> CountAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/v1/count/treatments/where{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        return rows.Count == 0 ? 0 : rows[0]!["count"]!.GetValue<long>();
    }

    [Fact]
    public async Task Count_AddsEveryServedTable_TheMealOnce()
    {
        var client = CreateAuthenticatedClient();
        await SeedAsync(client);

        (await CountAsync(client)).Should().Be(7);
    }

    [Fact]
    public async Task Count_InAWindow_AddsOnlyWhatStartsInside_TheMealOnce()
    {
        var client = CreateAuthenticatedClient();
        await SeedAsync(client);

        var since = Uri.EscapeDataString(MinutesAgo(120));
        (await CountAsync(client, $"?find[created_at][$gte]={since}")).Should().Be(4);
    }
}
