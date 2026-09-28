using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A v3 devicestatus PUT or DELETE acts on the whole stored document, through the real decomposer
/// and repositories, whether or not it was stored with a legacy id: a PUT rewrites each section it
/// carries in place under the status's identifier and removes each one it omits, and a DELETE
/// removes them all.
/// </summary>
[Trait("Category", "Integration")]
public class DeviceStatusV3StoredStatusIntegrationTests : ApiIntegrationTestBase
{
    public DeviceStatusV3StoredStatusIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static string Now(int minutesAgo) =>
        DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Snapshots written through v4 carry no legacy id, as connector statuses do. The PUT must find
    /// them by the identifier v3 serves, not insert a second set beside them.
    /// </summary>
    [Fact]
    public async Task Put_on_a_status_stored_without_a_legacy_id_rewrites_it_in_place()
    {
        var client = CreateAuthenticatedClient();
        var device = $"tandem://replace-{Guid.NewGuid():N}";
        var correlationId = Guid.NewGuid();
        var timestamp = Now(5);

        var pumpResponse = await client.PostAsJsonAsync("/api/v4/device-status/pump", new[]
        {
            new
            {
                timestamp, device, correlationId, dataSource = "tandem-connector", syncIdentifier = $"pump-{correlationId}",
                manufacturer = "Tandem", model = "t:slim X2", reservoir = 120.0,
            },
        });
        pumpResponse.StatusCode.Should().Be(HttpStatusCode.Created, await pumpResponse.Content.ReadAsStringAsync());
        var pumpId = Guid.Parse((await ReadJsonAsync(pumpResponse))![0]!["id"]!.GetValue<string>());
        var uploaderResponse = await client.PostAsJsonAsync("/api/v4/device-status/uploader", new[]
        {
            new { timestamp, device, correlationId, battery = 80 },
        });
        uploaderResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var identifier = MongoObjectId.FromGuid(pumpId);
        (await GetV3Async(client, identifier))!["uploader"].Should().NotBeNull();

        var put = await client.PutAsJsonAsync($"/api/v3/devicestatus/{identifier}", new
        {
            app = "t:connect",
            device,
            created_at = timestamp,
            pump = new { manufacturer = "Tandem", model = "t:slim X2", reservoir = 95.0 },
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var result = (await ReadJsonAsync(put))!["result"]!;
        result["identifier"]!.GetValue<string>().Should().Be(identifier);
        result["pump"]!["reservoir"]!.GetValue<double>().Should().Be(95.0);
        result["uploader"].Should().BeNull();

        var pumps = await ListV4Async(client, "pump", device);
        pumps.Should().ContainSingle();
        pumps[0]!["id"]!.GetValue<string>().Should().Be(pumpId.ToString());
        pumps[0]!["reservoir"]!.GetValue<double>().Should().Be(95.0);
        pumps[0]!["dataSource"]!.GetValue<string>().Should().Be("tandem-connector");
        (await ListV4Async(client, "uploader", device)).Should().BeEmpty();

        var fetched = await GetV3Async(client, identifier);
        fetched!["pump"]!["reservoir"]!.GetValue<double>().Should().Be(95.0);
        fetched["uploader"].Should().BeNull();
    }

    /// <summary>
    /// The sections a PUT drops are removed, and a later PUT that brings one back stores it again.
    /// </summary>
    [Fact]
    public async Task Put_drops_the_sections_it_omits_and_a_later_put_restores_them()
    {
        var client = CreateAuthenticatedClient();
        var device = $"openaps://replace-{Guid.NewGuid():N}";
        var id = MongoObjectId.NewObjectId();
        var createdAt = Now(7);

        var post = await client.PostAsJsonAsync("/api/v1/devicestatus", new
        {
            _id = id,
            device,
            created_at = createdAt,
            openaps = new { iob = new { iob = 1.5, time = createdAt } },
            pump = new { reservoir = 50.0, clock = createdAt },
            uploader = new { battery = 70 },
        });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        var stored = await GetV3Async(client, id);
        stored!["openaps"].Should().NotBeNull();
        stored["pump"].Should().NotBeNull();

        var drop = await client.PutAsJsonAsync($"/api/v3/devicestatus/{id}", new
        {
            app = "e2e", device, created_at = createdAt, uploader = new { battery = 42 },
        });
        drop.StatusCode.Should().Be(HttpStatusCode.OK, await drop.Content.ReadAsStringAsync());

        var dropped = await GetV3Async(client, id);
        dropped!["openaps"].Should().BeNull();
        dropped["pump"].Should().BeNull();
        dropped["uploader"]!["battery"]!.GetValue<int>().Should().Be(42);
        (await ListV4Async(client, "pump", device)).Should().BeEmpty();
        (await ListV4Async(client, "aps", device)).Should().BeEmpty();

        var restore = await client.PutAsJsonAsync($"/api/v3/devicestatus/{id}", new
        {
            app = "e2e", device, created_at = createdAt,
            pump = new { reservoir = 30.0, clock = createdAt },
            uploader = new { battery = 41 },
        });
        restore.StatusCode.Should().Be(HttpStatusCode.OK, await restore.Content.ReadAsStringAsync());

        var restored = await GetV3Async(client, id);
        restored!["identifier"]!.GetValue<string>().Should().Be(id);
        restored["pump"]!["reservoir"]!.GetValue<double>().Should().Be(30.0);
        restored["uploader"]!["battery"]!.GetValue<int>().Should().Be(41);
        (await ListV4Async(client, "pump", device)).Should().ContainSingle();
        (await ListV4Async(client, "uploader", device)).Should().ContainSingle();
    }

    /// <summary>
    /// A user's DELETE removes every snapshot of a status stored without a legacy id, leaving
    /// tombstones that refuse the connector's re-upload and an audit entry per row.
    /// </summary>
    [Fact]
    public async Task Delete_on_a_status_stored_without_a_legacy_id_removes_the_whole_status()
    {
        var client = CreateAuthenticatedClient();
        var device = $"tandem://delete-{Guid.NewGuid():N}";
        var correlationId = Guid.NewGuid();
        var timestamp = Now(9);
        var before = DateTime.UtcNow.AddMinutes(-1);
        var pump = new
        {
            timestamp, device, correlationId, dataSource = "tandem-connector", syncIdentifier = $"pump-{correlationId}",
            manufacturer = "Tandem", model = "t:slim X2", reservoir = 120.0,
        };

        var pumpResponse = await client.PostAsJsonAsync("/api/v4/device-status/pump", new[] { pump });
        pumpResponse.StatusCode.Should().Be(HttpStatusCode.Created, await pumpResponse.Content.ReadAsStringAsync());
        var pumpId = Guid.Parse((await ReadJsonAsync(pumpResponse))![0]!["id"]!.GetValue<string>());
        (await client.PostAsJsonAsync("/api/v4/device-status/uploader", new[]
        {
            new { timestamp, device, correlationId, battery = 80 },
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var identifier = MongoObjectId.FromGuid(pumpId);
        var delete = await client.DeleteAsync($"/api/v3/devicestatus/{identifier}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync());
        (await client.GetAsync($"/api/v3/devicestatus/{identifier}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListV4Async(client, "pump", device)).Should().BeEmpty();
        (await ListV4Async(client, "uploader", device)).Should().BeEmpty();

        var resync = await client.PostAsJsonAsync("/api/v4/device-status/pump", new[] { pump });
        resync.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ListV4Async(client, "pump", device)).Should().BeEmpty("a user delete holds the connector's sync key");

        var audit = await client.GetAsync(
            $"/api/v4/audit/mutations?from={before:O}&to={DateTime.UtcNow.AddMinutes(1):O}&action=delete&entityId={pumpId}");
        audit.StatusCode.Should().Be(HttpStatusCode.OK, await audit.Content.ReadAsStringAsync());
        (await ReadJsonAsync(audit))!["data"]!.AsArray().Should().ContainSingle();

        (await client.DeleteAsync($"/api/v3/devicestatus/{identifier}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An override uploaded with a status that has no id is stored under the key the status is
    /// addressed by, so a PUT that leaves it out removes it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Override_uploaded_with_an_id_less_status_goes_with_a_put_that_omits_it(bool batch)
    {
        var client = CreateAuthenticatedClient();
        var device = $"loop://override-{Guid.NewGuid():N}";
        var createdAt = Now(batch ? 14 : 11);

        var status = new DeviceStatus
        {
            Device = device,
            CreatedAt = createdAt,
            Mills = DateTimeOffset.Parse(createdAt).ToUnixTimeMilliseconds(),
            OpenAps = new OpenApsStatus { Iob = new OpenApsIobData { Iob = 1.5 } },
            Override = new OverrideStatus { Active = true, Name = "Exercise", Duration = 60, Timestamp = createdAt },
        };
        await WithScopeAsync(sp =>
        {
            var decomposer = sp.GetRequiredService<IDeviceStatusDecomposer>();
            return batch
                ? decomposer.DecomposeBatchAsync([status], source: "integration-connector", WriteOrigin.Live)
                : decomposer.DecomposeAsync(status, source: "integration-connector", WriteOrigin.Live);
        });

        var aps = await ListV4Async(client, "aps", device);
        aps.Should().ContainSingle();
        var identifier = MongoObjectId.FromGuid(Guid.Parse(aps[0]!["id"]!.GetValue<string>()));
        (await OverridesUnderAsync(identifier, createdAt)).Should().ContainSingle();

        var put = await client.PutAsJsonAsync($"/api/v3/devicestatus/{identifier}", new
        {
            app = "e2e", device, created_at = createdAt, openaps = new { iob = new { iob = 2.0 } },
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await OverridesUnderAsync(identifier, createdAt)).Should().BeEmpty();
    }

    private Task<List<StateSpan>> OverridesUnderAsync(string key, string at) => WithScopeAsync(async sp =>
    {
        var timestamp = DateTimeOffset.Parse(at).UtcDateTime;
        var spans = await sp.GetRequiredService<IStateSpanService>().GetStateSpansAsync(
            category: StateSpanCategory.Override, from: timestamp.AddMinutes(-1), to: timestamp.AddMinutes(1), count: 100);
        return spans.Where(s => s.OriginalId == key).ToList();
    });

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;
        return await action(scope.ServiceProvider);
    }

    private static async Task<JsonNode?> GetV3Async(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/api/v3/devicestatus/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response))!["result"];
    }

    private static async Task<JsonArray> ListV4Async(HttpClient client, string kind, string device)
    {
        var response = await client.GetAsync(
            $"/api/v4/device-status/{kind}?device={Uri.EscapeDataString(device)}&limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response))!["data"]!.AsArray();
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());
}
