using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A v1 Temp Basal with no duration: stored as a cancel that ends where it starts, and read back
/// as it was uploaded.
/// </summary>
[Trait("Category", "Integration")]
public class TempBasalCancelIntegrationTests : ApiIntegrationTestBase
{
    public TempBasalCancelIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AV1Cancel_IsStoredEndingWhereItStartsAndReadsBackAsUploaded(bool sendsDuration)
    {
        var client = CreateAuthenticatedClient();
        var at = DateTimeOffset.UtcNow.AddHours(-3).AddMilliseconds(Random.Shared.Next(1, 60_000));
        var createdAt = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var cancel = new Dictionary<string, object>
        {
            ["eventType"] = "Temp Basal",
            ["created_at"] = createdAt,
            ["enteredBy"] = "openaps://AndroidAPS",
            ["absolute"] = 0,
        };
        if (sendsDuration)
            cancel["duration"] = 0;

        (await client.PostAsJsonAsync("/api/v1/treatments", cancel)).EnsureSuccessStatusCode();

        var stored = await WithTenantScopeAsync(async sp => (await sp.GetRequiredService<ITempBasalRepository>().GetAsync(
            from: at.UtcDateTime.AddSeconds(-1), to: at.UtcDateTime.AddSeconds(1), device: null, source: null,
            limit: 10, offset: 0, descending: true, ct: default)).Single());
        stored.EndMills.Should().Be(stored.StartMills);

        var served = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/treatments?find[created_at]={Uri.EscapeDataString(createdAt)}")).EnumerateArray().Single();
        served.GetProperty("eventType").GetString().Should().Be("Temp Basal");
        served.GetProperty("created_at").GetString().Should().Be(createdAt);
        served.GetProperty("duration").GetDouble().Should().Be(0);
        served.GetProperty("absolute").GetDouble().Should().Be(0);
    }
}
