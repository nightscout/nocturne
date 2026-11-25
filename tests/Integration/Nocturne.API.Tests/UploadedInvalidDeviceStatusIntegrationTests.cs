using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A device status uploaded with <c>isValid: false</c> is stored deleted, as Nightscout stores it: no
/// search serves it, v3 history serves it as deleted, and an upload of its id without <c>isValid</c>
/// brings it back. Only the client's own id deletes a live status: a status at the same time is
/// another one.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class UploadedInvalidDeviceStatusIntegrationTests : ApiIntegrationTestBase
{
    public UploadedInvalidDeviceStatusIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private readonly DateTimeOffset _at = DateTimeOffset.UtcNow.AddMinutes(-10);

    private JsonObject Status(string? id = null, bool? isValid = null, double reservoir = 120)
    {
        var createdAt = _at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var status = new JsonObject
        {
            ["app"] = "AAPS",
            ["device"] = "openaps://AndroidAPS",
            ["created_at"] = createdAt,
            ["date"] = _at.ToUnixTimeMilliseconds(),
            ["pump"] = new JsonObject { ["reservoir"] = reservoir, ["clock"] = createdAt },
            ["uploader"] = new JsonObject { ["battery"] = 80 },
            ["openaps"] = new JsonObject
            {
                ["iob"] = new JsonObject { ["iob"] = 1.2, ["timestamp"] = createdAt },
                ["suggested"] = new JsonObject { ["bg"] = 120, ["timestamp"] = createdAt },
            },
            ["xdripjs"] = new JsonObject { ["state"] = 6 },
        };
        if (id is not null)
            status["_id"] = id;
        if (isValid is { } valid)
            status["isValid"] = valid;
        return status;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];

    private static async Task UploadAsync(HttpClient client, JsonObject status, bool v3)
    {
        var response = v3
            ? await client.PostAsJsonAsync("/api/v3/devicestatus", status)
            : await client.PostAsJsonAsync("/api/v1/devicestatus", new JsonArray(status.DeepClone()));
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>The ids v1 search, v3 search and v3 search by modification serve.</summary>
    private sealed record Live(string[] V1, string[] V3, string[] V3Modified)
    {
        public static Live None { get; } = new([], [], []);
    }

    private async Task<Live> LiveAsync(HttpClient client)
    {
        static string[] Ids(JsonArray documents, string key) =>
            [.. documents.Select(d => d![key]!.GetValue<string>()).Order()];

        await SettleHistoryAsync();
        return new Live(
            Ids((await GetAsync(client, "/api/v1/devicestatus?count=100")).AsArray(), "_id"),
            Ids((await GetAsync(client, "/api/v3/devicestatus?limit=100"))["result"]!.AsArray(), "identifier"),
            Ids((await GetAsync(client, "/api/v3/devicestatus?limit=100&srvModified$gt=0&sort=srvModified"))["result"]!.AsArray(),
                "identifier"));
    }

    private static void ShouldServe(Live live, string id) =>
        live.Should().BeEquivalentTo(new Live([id], [id], [id]), o => o.WithStrictOrdering());

    private async Task<JsonArray> HistoryAsync(HttpClient client)
    {
        await SettleHistoryAsync();
        return (await GetAsync(client, "/api/v3/devicestatus/history/0"))["result"]!.AsArray();
    }

    private static bool IsDeleted(JsonNode? status) => status?["isValid"]?.GetValue<bool>() == false;

    private static bool Is(JsonNode? status, string id) => status?["identifier"]?.GetValue<string>() == id;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_upload_with_isValid_false_is_stored_deleted(bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();

        await UploadAsync(client, Status(id, isValid: false), v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        var deleted = (await HistoryAsync(client)).Should().ContainSingle().Subject;
        IsDeleted(deleted).Should().BeTrue();
        Is(deleted, id).Should().BeTrue();
        deleted!["pump"]!["reservoir"]!.GetValue<double>().Should().Be(120);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resend_without_isValid_brings_it_back(bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id, isValid: false), v3);

        await UploadAsync(client, Status(id), v3);

        ShouldServe(await LiveAsync(client), id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resend_with_isValid_false_stores_nothing_more(bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id, isValid: false), v3);

        await UploadAsync(client, Status(id, isValid: false), v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().ContainSingle().Which.Should().Match<JsonNode>(s => IsDeleted(s));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_upload_with_isValid_false_deletes_the_live_status_its_id_names(bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id), v3);
        ShouldServe(await LiveAsync(client), id);

        await UploadAsync(client, Status(id, isValid: false), v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().Contain(s => IsDeleted(s) && Is(s, id));

        await UploadAsync(client, Status(id), v3);

        ShouldServe(await LiveAsync(client), id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_upload_with_isValid_false_under_another_id_leaves_the_live_status_at_its_time(bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id), v3);

        await UploadAsync(client, Status(NewId(), isValid: false, reservoir: 90), v3);
        await UploadAsync(client, Status(isValid: false, reservoir: 80), v3);

        ShouldServe(await LiveAsync(client), id);
        (await HistoryAsync(client)).Count(IsDeleted).Should().Be(2);
    }

    [Fact]
    public async Task A_v3_update_that_sets_isValid_false_is_refused()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id), v3: true);

        var put = await client.PutAsJsonAsync($"/api/v3/devicestatus/{id}", Status(id, isValid: false));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(await put.Content.ReadAsStringAsync())!["message"]!.GetValue<string>()
            .Should().Be("Field isValid cannot be modified by the client");
        ShouldServe(await LiveAsync(client), id);
    }

    /// <summary>
    /// An upload that deletes the live status its id names is announced, through the device status
    /// service's events, as that status's delete and as nothing else. One stored deleted is not
    /// announced at all.
    /// </summary>
    [Fact]
    public async Task An_upload_that_deletes_a_live_status_is_announced_as_its_delete()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id), v3: false);
        var events = new RecordedEvents();

        await using (var scope = OpenTenantScope())
        {
            var service = ActivatorUtilities.CreateInstance<DeviceStatusService>(
                scope.ServiceProvider, (IDataEventSink<DeviceStatus>)events);
            await service.CreateDeviceStatusesAsync([InvalidStatus(id), InvalidStatus(NewId())]);
        }

        var deleted = events.Deleted.Should().ContainSingle().Subject!;
        deleted.Id.Should().Be(id);
        deleted.Pump!.Reservoir.Should().Be(120);
        deleted.IsValid.Should().BeFalse();
        events.Created.Should().BeEmpty();
        events.Updated.Should().BeEmpty();
        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
    }

    /// <summary>
    /// A status stored deleted reaches no one a live write reaches: no record change names its
    /// snapshots, so SignalR and the alert facts never see them.
    /// </summary>
    [Fact]
    public async Task A_status_stored_deleted_announces_no_snapshot()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        var writes = new RecordedWrites();

        await using (var scope = OpenTenantScope())
        {
            IRecordChangePublisher changes = writes.Changes;
            var sp = scope.ServiceProvider;
            var decomposer = ActivatorUtilities.CreateInstance<DeviceStatusDecomposer>(
                sp,
                (IApsSnapshotRepository)ActivatorUtilities.CreateInstance<ApsSnapshotRepository>(sp, changes),
                (IPumpSnapshotRepository)ActivatorUtilities.CreateInstance<PumpSnapshotRepository>(sp, changes),
                (IUploaderSnapshotRepository)ActivatorUtilities.CreateInstance<UploaderSnapshotRepository>(sp, changes));
            await decomposer.DecomposeBatchAsync([Parsed(Status(id, isValid: false))], source: null, WriteOrigin.Live);
        }

        writes.Changes.Batches.SelectMany(b => b.Changes).Select(c => c.RecordType).Should()
            .BeEquivalentTo([typeof(V4Models.ApsSnapshot), typeof(V4Models.PumpSnapshot), typeof(V4Models.UploaderSnapshot)],
                "the write still reports each snapshot type to the caches");
        writes.Created.Should().BeEmpty();
        writes.Updated.Should().BeEmpty();
        writes.Deleted.Should().BeEmpty();
        var deleted = (await HistoryAsync(client)).Should().ContainSingle().Subject;
        (IsDeleted(deleted) && Is(deleted, id)).Should().BeTrue();
        deleted!["uploader"]!["battery"]!.GetValue<double>().Should().Be(80);
    }

    /// <summary>
    /// A connector, the sample data feed or an import sends statuses with no id. One sent with
    /// <c>isValid: false</c> names no status, so it writes nothing, alone in its batch or not.
    /// </summary>
    [Fact]
    public async Task An_in_process_upload_with_isValid_false_and_no_id_writes_nothing()
    {
        var client = CreateAuthenticatedClient();

        var written = await WithTenantScopeAsync(sp =>
            sp.GetRequiredService<IDeviceStatusService>().CreateDeviceStatusesAsync([Parsed(Status(isValid: false))]));

        written.Invalidated.Should().BeEmpty();
        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
        (await HistoryAsync(client)).Should().BeEmpty();
    }

    /// <summary>
    /// A v3 read resolves a status by its snapshot's own uuid, the write only by its legacy id. An
    /// invalid upload under that uuid deletes nothing, so it is answered as a create, not as a
    /// duplicate of the status it left live.
    /// </summary>
    [Fact]
    public async Task A_v3_create_that_names_no_status_it_deletes_is_answered_as_a_create()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id), v3: false);
        var uuid = (await WithTenantScopeAsync(sp => sp.GetRequiredService<IPumpSnapshotRepository>().GetByLegacyIdAsync(id)))!.Id
            .ToString();
        await GetAsync(client, $"/api/v3/devicestatus/{uuid}");

        var response = await client.PostAsJsonAsync("/api/v3/devicestatus", Status(uuid, isValid: false));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["isDeduplication"]?.GetValue<bool>()
            .Should().NotBe(true);
        ShouldServe(await LiveAsync(client), id);
    }

    [Fact]
    public async Task A_v3_create_that_deletes_the_status_its_id_names_is_answered_as_its_duplicate()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Status(id), v3: false);

        var response = await client.PostAsJsonAsync("/api/v3/devicestatus", Status(id, isValid: false));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["isDeduplication"]!.GetValue<bool>().Should().BeTrue();
        body["identifier"]!.GetValue<string>().Should().Be(id);
        (await LiveAsync(client)).Should().BeEquivalentTo(Live.None);
    }

    private static DeviceStatus Parsed(JsonObject status) => JsonSerializer.Deserialize<DeviceStatus>(status.ToJsonString())!;

    private DeviceStatus InvalidStatus(string id) => new()
    {
        Id = id,
        Device = "openaps://AndroidAPS",
        CreatedAt = _at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        Mills = _at.ToUnixTimeMilliseconds(),
        Pump = new PumpStatus { Reservoir = 60 },
        IsValid = false,
    };

    private AsyncServiceScope OpenTenantScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:uploaded-invalid");

    private sealed class RecordedEvents : IDataEventSink<DeviceStatus>
    {
        public List<DeviceStatus> Created { get; } = [];
        public List<DeviceStatus> Updated { get; } = [];
        public List<DeviceStatus?> Deleted { get; } = [];

        public Task OnCreatedAsync(IReadOnlyList<DeviceStatus> items, CancellationToken ct = default)
        {
            Created.AddRange(items);
            return Task.CompletedTask;
        }

        public Task OnCreatedAsync(DeviceStatus item, CancellationToken ct = default)
        {
            Created.Add(item);
            return Task.CompletedTask;
        }

        public Task OnUpdatedAsync(DeviceStatus item, CancellationToken ct = default)
        {
            Updated.Add(item);
            return Task.CompletedTask;
        }

        public Task OnDeletedAsync(DeviceStatus? item, CancellationToken ct = default)
        {
            Deleted.Add(item);
            return Task.CompletedTask;
        }
    }
}
