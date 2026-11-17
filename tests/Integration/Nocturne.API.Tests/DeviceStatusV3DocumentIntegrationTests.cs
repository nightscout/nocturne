using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A v3 devicestatus search, read and history return every key the upload carried, AAPS
/// <c>configuration</c> and <c>app</c> included, and <c>fields=</c> projects extension keys.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class DeviceStatusV3DocumentIntegrationTests : ApiIntegrationTestBase
{
    private static readonly string[] UploadedKeys =
        ["configuration", "app", "date", "isCharging", "override"];

    public DeviceStatusV3DocumentIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private async Task<(HttpClient Client, string Device, string Identifier)> UploadAsync()
    {
        var client = CreateAuthenticatedClient();
        var device = $"openaps://aaps-{Guid.NewGuid():N}";
        var created = DateTime.UtcNow.AddMinutes(-2);

        var post = await client.PostAsJsonAsync("/api/v3/devicestatus", new
        {
            app = "AAPS",
            device,
            created_at = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            date = new DateTimeOffset(created).ToUnixTimeMilliseconds(),
            isCharging = true,
            @override = new { active = true, name = "Exercise", multiplier = 1.5, timestamp = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
            configuration = new { pump = "Combo", insulin = 5 },
            uploader = new { battery = 80, isCharging = true },
            loop = new { timestamp = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), iob = new { iob = 1.2, timestamp = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") } },
            pump = new { reservoir = 120.0 },
        });
        post.StatusCode.Should().Be(HttpStatusCode.Created, await post.Content.ReadAsStringAsync());
        var identifier = (await ReadAsync(post))!["identifier"]!.GetValue<string>();
        return (client, device, identifier);
    }

    private static void AssertEveryKey(JsonNode? document, string identifier)
    {
        var keys = document!.AsObject().Select(p => p.Key).ToList();
        keys.Should().Contain(UploadedKeys);
        keys.Should().Contain(["_id", "identifier", "device", "created_at", "pump"]);
        document["_id"]!.GetValue<string>().Should().Be(identifier);
        document["identifier"]!.GetValue<string>().Should().Be(identifier);
        document["app"]!.GetValue<string>().Should().Be("AAPS");
        document["isCharging"]!.GetValue<bool>().Should().BeTrue();
        document["override"]!["multiplier"]!.GetValue<double>().Should().Be(1.5);
        document["configuration"]!["pump"]!.GetValue<string>().Should().Be("Combo");
        document["configuration"]!["insulin"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task Search_returns_every_uploaded_key()
    {
        var (client, device, identifier) = await UploadAsync();

        var response = await client.GetAsync($"/api/v3/devicestatus?device$eq={Uri.EscapeDataString(device)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = (await ReadAsync(response))!["result"]!.AsArray();
        AssertEveryKey(result.Should().ContainSingle().Subject, identifier);
    }

    [Fact]
    public async Task Read_returns_every_uploaded_key()
    {
        var (client, _, identifier) = await UploadAsync();

        var response = await client.GetAsync($"/api/v3/devicestatus/{identifier}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        AssertEveryKey((await ReadAsync(response))!["result"], identifier);
    }

    [Fact]
    public async Task History_returns_every_uploaded_key()
    {
        var (client, device, identifier) = await UploadAsync();

        var response = await client.GetAsync("/api/v3/devicestatus/history/0?limit=1000");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = (await ReadAsync(response))!["result"]!.AsArray();
        AssertEveryKey(result.Single(d => d!["device"]!.GetValue<string>() == device), identifier);
    }

    [Fact]
    public async Task Fields_projects_extension_keys()
    {
        var (client, device, _) = await UploadAsync();

        var response = await client.GetAsync(
            $"/api/v3/devicestatus?device$eq={Uri.EscapeDataString(device)}&fields=configuration,app");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var document = (await ReadAsync(response))!["result"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        document.Select(p => p.Key).Should().BeEquivalentTo(["configuration", "app"]);
        document["configuration"]!["pump"]!.GetValue<string>().Should().Be("Combo");
    }

    [Fact]
    public async Task Stored_extras_naming_typed_properties_do_not_break_reads()
    {
        var timestamp = DateTime.UtcNow.AddMinutes(-3);
        var millis = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
        var client = CreateAuthenticatedClient();
        await WithTenantScopeAsync(services =>
            services.GetRequiredService<IDeviceStatusExtrasRepository>().CreateAsync(
                new DeviceStatusExtras
                {
                    Id = Guid.CreateVersion7(),
                    CorrelationId = Guid.NewGuid(),
                    Timestamp = timestamp,
                    Extras = new Dictionary<string, object?>
                    {
                        ["createdAt"] = "1999-01-01T00:00:00.000Z",
                        ["date"] = 42L,
                        ["_id"] = "colliding",
                        ["isValid"] = false,
                        ["srvModified"] = 7L,
                        ["vendorNote"] = "kept",
                    },
                },
                WriteOrigin.Live));

        var search = await client.GetAsync("/api/v3/devicestatus?limit=1000");
        search.StatusCode.Should().Be(HttpStatusCode.OK, await search.Content.ReadAsStringAsync());
        var fromSearch = (await ReadAsync(search))!["result"]!.AsArray().Should().ContainSingle().Subject!;
        var identifier = fromSearch["identifier"]!.GetValue<string>();

        var byId = await client.GetAsync($"/api/v3/devicestatus/{identifier}");
        byId.StatusCode.Should().Be(HttpStatusCode.OK, await byId.Content.ReadAsStringAsync());
        var history = await client.GetAsync("/api/v3/devicestatus/history/0?limit=1000");
        history.StatusCode.Should().Be(HttpStatusCode.OK, await history.Content.ReadAsStringAsync());
        var fromHistory = (await ReadAsync(history))!["result"]!.AsArray().Should().ContainSingle().Subject!;

        foreach (var document in new[] { fromSearch, (await ReadAsync(byId))!["result"]!, fromHistory })
        {
            document["_id"]!.GetValue<string>().Should().Be(identifier);
            document["date"]!.GetValue<long>().Should().Be(millis);
            document["created_at"]!.GetValue<string>().Should().NotStartWith("1999");
            document["createdAt"]!.GetValue<string>().Should().Be("1999-01-01T00:00:00.000Z");
            document["srvModified"]!.GetValue<long>().Should().NotBe(7);
            document["isValid"].Should().BeNull();
            document["vendorNote"]!.GetValue<string>().Should().Be("kept");
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());
}
