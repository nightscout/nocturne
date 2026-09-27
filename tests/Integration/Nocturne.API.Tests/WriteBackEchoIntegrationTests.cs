using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A record stored with no legacy id goes out on the wire under its own uuid, which is the id
/// Nightscout write-back sends it upstream under; the Nightscout connector then pulls the copy back
/// through the same decomposers a v1 upload reaches. Replaying that copy as a v1 upload exercises
/// the round trip end to end against Postgres (#1804).
/// </summary>
[Trait("Category", "Integration")]
public class WriteBackEchoIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private sealed record V1Record(string Id, string Device);

    /// <remarks>
    /// The pulled-back copy is moved past the window in which the v1 upload path drops an entry
    /// matching a stored one on device, type, value and time before any id is looked at. The
    /// connector path runs no such check, and the copy is matched here by id alone.
    /// </remarks>
    private static object Reading(string device, long date, int sgv, string id) =>
        new { _id = id, type = "sgv", sgv, date, dateString = DateTimeOffset.FromUnixTimeMilliseconds(date).ToString("O"), device };

    private async Task<List<V1Record>> V1Async(string collection, string device)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/{collection}.json?count=1000");
        return body.EnumerateArray()
            .Where(r => r.TryGetProperty("device", out var d) && d.GetString() == device)
            .Select(r => new V1Record(r.GetProperty("_id").GetString()!, device))
            .ToList();
    }

    private async Task<List<(Guid Id, double Mgdl)>> LiveSensorReadingsAsync(string device)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v4/glucose/sensor?limit=1000&device={Uri.EscapeDataString(device)}");
        return body.GetProperty("data").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetGuid(), r.GetProperty("mgdl").GetDouble()))
            .ToList();
    }

    private async Task<int> LiveApsSnapshotsAsync(string device)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v4/device-status/aps?limit=1000&device={Uri.EscapeDataString(device)}");
        return body.GetProperty("data").GetArrayLength();
    }

    private async Task<Guid> CreateUnkeyedReadingAsync(string device, long date)
    {
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/glucose/sensor", new
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(date),
            device,
            mgdl = 111,
        });
        created.IsSuccessStatusCode.Should().BeTrue();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static long PastTheV1DuplicateWindow(long date) => date + (long)TimeSpan.FromMinutes(6).TotalMilliseconds;

    /// <summary>
    /// The uuid is what write-back sends today; the 24-hex prefix is what every v1/v3 read serves
    /// for the reading, and so what a client echoing a read sends.
    /// </summary>
    public static TheoryData<string> WireForms => new() { "uuid", "prefix" };

    private static string WireId(Guid id, string form) => form == "uuid" ? id.ToString() : MongoObjectId.FromGuid(id);

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task APulledBackReading_UpdatesTheReadingItCameFrom(string form)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var id = await CreateUnkeyedReadingAsync(device, date);
        var servedAs = (await V1Async("entries", device)).Single().Id;

        var echoed = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v1/entries", new[] { Reading(device, PastTheV1DuplicateWindow(date), sgv: 112, WireId(id, form)) });

        echoed.IsSuccessStatusCode.Should().BeTrue();
        (await LiveSensorReadingsAsync(device)).Should().Equal((id, 112d));
        (await V1Async("entries", device)).Select(r => r.Id).Should().Equal(servedAs);
    }

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task APulledBackReadingTheUserDeleted_StaysDeleted(string form)
    {
        var device = $"echo-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.AddMinutes(-40).ToUnixTimeMilliseconds();
        var id = await CreateUnkeyedReadingAsync(device, date);
        (await AuthenticatedClient.DeleteAsync($"/api/v4/glucose/sensor/{id}")).IsSuccessStatusCode.Should().BeTrue();

        await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[] { Reading(device, PastTheV1DuplicateWindow(date), sgv: 112, WireId(id, form)) });

        (await LiveSensorReadingsAsync(device)).Should().BeEmpty();
        (await V1Async("entries", device)).Should().BeEmpty();
    }

    [Fact]
    public async Task APulledBackStatus_UpdatesTheStatusItCameFrom()
    {
        var device = $"openaps://echo-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow.AddMinutes(-20);
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/device-status/aps", new[]
        {
            new { timestamp = at, device, iob = 1.5 },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await V1Async("devicestatus", device);
        stored.Should().ContainSingle();
        var wireId = stored[0].Id;

        var echoed = await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", new
        {
            _id = wireId,
            device,
            created_at = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            openaps = new { iob = new { iob = 1.5, timestamp = at.ToString("O") } },
        });

        echoed.IsSuccessStatusCode.Should().BeTrue();
        (await LiveApsSnapshotsAsync(device)).Should().Be(1);
        (await V1Async("devicestatus", device)).Select(r => r.Id).Should().Equal(wireId);
    }

    [Fact]
    public async Task AnIdLessStatusUpload_IsStoredUnderTheIdItIsServedAndWrittenBackUnder()
    {
        var device = $"openaps://idless-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var status = new { device, created_at = at, openaps = new { iob = new { iob = 0.4, timestamp = at } } };

        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", status);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString();

        id.Should().MatchRegex("^[0-9a-f]{24}$");
        (await V1Async("devicestatus", device)).Select(r => r.Id).Should().Equal(id);

        await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", new { _id = id, status.device, status.created_at, status.openaps });
        (await LiveApsSnapshotsAsync(device)).Should().Be(1);
    }
}
