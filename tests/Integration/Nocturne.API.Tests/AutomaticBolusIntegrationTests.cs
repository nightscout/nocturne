using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Loop uploads its automatic boluses as <c>Correction Bolus</c> with <c>automatic: true</c>. They
/// are stored as algorithm boluses, counted as automatic delivery, and still read back from v1 as
/// the event type and flag that were uploaded.
/// </summary>
[Trait("Category", "Integration")]
public class AutomaticBolusIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task Loop_automatic_correction_bolus_is_stored_as_algorithm_and_reads_back_unchanged_from_v1()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(1, 12)
            .Select(i => new { type = "sgv", sgv = 100 + i, date = now.AddMinutes(-5 * i).ToUnixTimeMilliseconds() })
            .ToArray();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", entries)).EnsureSuccessStatusCode();
        var treatments = new object[]
        {
            new
            {
                eventType = "Correction Bolus", insulin = 0.4, automatic = true,
                enteredBy = "loop://iPhone", created_at = now.AddMinutes(-20).ToString("O"),
            },
            new
            {
                eventType = "Correction Bolus", insulin = 2.0, automatic = false,
                enteredBy = "loop://iPhone", created_at = now.AddMinutes(-40).ToString("O"),
            },
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", treatments)).EnsureSuccessStatusCode();

        var legacy = await GetJsonAsync("/api/v1/treatments");
        var automatic = legacy.EnumerateArray().Single(t => t.GetProperty("insulin").GetDouble() == 0.4);
        automatic.GetProperty("eventType").GetString().Should().Be("Correction Bolus");
        automatic.GetProperty("automatic").GetBoolean().Should().BeTrue();
        var manual = legacy.EnumerateArray().Single(t => t.GetProperty("insulin").GetDouble() == 2.0);
        manual.GetProperty("eventType").GetString().Should().Be("Correction Bolus");
        manual.GetProperty("automatic").ValueKind.Should().NotBe(JsonValueKind.True);

        var stored = (await GetJsonAsync("/api/v4/insulin/boluses")).GetProperty("data").EnumerateArray().ToList();
        stored.Single(b => b.GetProperty("insulin").GetDouble() == 0.4).GetProperty("kind").GetString().Should().Be("Algorithm");
        stored.Single(b => b.GetProperty("insulin").GetDouble() == 2.0).GetProperty("kind").GetString().Should().Be("Manual");

        var delivery = (await GetJsonAsync("/api/v4/statistics/periods")).GetProperty("lastDay").GetProperty("insulinDelivery");
        delivery.GetProperty("automaticBolus").GetDouble().Should().BeApproximately(0.4, 1e-9);
        delivery.GetProperty("manualBolus").GetDouble().Should().BeApproximately(2.0, 1e-9);
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await AuthenticatedClient.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
