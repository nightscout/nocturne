using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A carb uploaded through v1 with a <c>foodType</c> shows the same label on the v4 carb reads.
/// Each test anchors at its own year so the rows one test writes sit outside every window another reads.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class CarbFoodTypeIntegrationTests : ApiIntegrationTestBase
{
    public CarbFoodTypeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static DateTimeOffset Anchor(int year) => new(year, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static string At(DateTimeOffset time) =>
        time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private async Task PostCarbAsync(DateTimeOffset time, string? foodType)
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/treatments", new
        {
            eventType = "Carb Correction",
            carbs = 6,
            foodType,
            created_at = At(time),
            enteredBy = "test",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<JsonElement[]> ReadV4CarbsAsync(DateTimeOffset time)
    {
        var from = Uri.EscapeDataString(At(time.AddHours(-1)));
        var to = Uri.EscapeDataString(At(time.AddHours(1)));
        var response = await CreateAuthenticatedClient().GetAsync($"/api/v4/nutrition/carbs?from={from}&to={to}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private async Task<string?> ReadV1FoodTypeAsync(DateTimeOffset time)
    {
        var from = Uri.EscapeDataString(At(time.AddHours(-1)));
        var to = Uri.EscapeDataString(At(time.AddHours(1)));
        var response = await CreateAuthenticatedClient().GetAsync(
            $"/api/v1/treatments?find[created_at][$gte]={from}&find[created_at][$lte]={to}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var treatment = doc.RootElement.EnumerateArray().Single();
        return treatment.TryGetProperty("foodType", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    [Fact]
    public async Task V1UploadedFoodType_IsOnTheV4ListAndById_AndMatchesV1()
    {
        var time = Anchor(2031);
        await PostCarbAsync(time, "🌮");

        var carbs = await ReadV4CarbsAsync(time);

        var carb = carbs.Should().ContainSingle().Subject;
        carb.GetProperty("foodType").GetString().Should().Be("🌮");
        (await ReadV1FoodTypeAsync(time)).Should().Be("🌮");

        var byId = await CreateAuthenticatedClient().GetAsync($"/api/v4/nutrition/carbs/{carb.GetProperty("id").GetGuid()}");
        byId.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await byId.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("foodType").GetString().Should().Be("🌮");
    }

    [Fact]
    public async Task SeveralFoodLines_GiveTheSameLabelOnV1AndV4()
    {
        var time = Anchor(2033);
        await PostCarbAsync(time, "🌮");
        var carbId = (await ReadV4CarbsAsync(time)).Single().GetProperty("id").GetGuid();

        var added = await CreateAuthenticatedClient().PostAsJsonAsync(
            $"/api/v4/nutrition/carbs/{carbId}/foods", new { carbs = 4, note = "rice" });
        added.StatusCode.Should().Be(HttpStatusCode.OK);

        var v4 = (await ReadV4CarbsAsync(time)).Single().GetProperty("foodType").GetString();
        v4.Should().Contain("🌮").And.Contain("rice").And.Contain(", ");
        (await ReadV1FoodTypeAsync(time)).Should().Be(v4);
    }

    [Fact]
    public async Task CarbWithNoFoodType_HasNullFoodTypeOnV4()
    {
        var time = Anchor(2032);
        await PostCarbAsync(time, null);

        var carbs = await ReadV4CarbsAsync(time);

        carbs.Should().ContainSingle().Which.GetProperty("foodType").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
