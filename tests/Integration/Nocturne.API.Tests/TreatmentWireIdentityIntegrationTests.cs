using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment carries one <c>_id</c> on the create response, the realtime events and the v1 read, and
/// a re-upload the user's delete refused is answered under the deleted record's id but neither stored nor broadcast.
/// </summary>
[Trait("Category", "Integration")]
public class TreatmentWireIdentityIntegrationTests : ApiIntegrationTestBase
{
    public TreatmentWireIdentityIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<(string Method, JsonElement Payload)> _events = new();

    private async Task ListenAsync()
    {
        var connection = await CreateDataHubConnectionAsync();
        foreach (var method in new[] { "create", "update", "delete" })
            connection.On<JsonElement>(method, payload => _events.Enqueue((method, payload)));
        await connection.StartAsync();
        await AuthorizeConnectionAsync(connection);
        await SubscribeToCollectionsAsync(connection, ["treatments"]);
    }

    private List<JsonElement> Events(string method, string syncIdentifier) =>
        _events
            .Where(e => e.Method == method && SyncIdentifierOf(e.Payload.GetProperty("doc")) == syncIdentifier)
            .Select(e => e.Payload)
            .ToList();

    private async Task<JsonElement> WaitForAsync(string method, string syncIdentifier)
    {
        (await WaitForEventAsync(() => Events(method, syncIdentifier).Count > 0, EventTimeout))
            .Should().BeTrue($"the {method} event must be broadcast");
        return Events(method, syncIdentifier)[0];
    }

    private static string? SyncIdentifierOf(JsonElement treatment) =>
        treatment.ValueKind == JsonValueKind.Object && treatment.TryGetProperty("syncIdentifier", out var id)
            ? id.GetString()
            : null;

    private static Dictionary<string, object> LoopBolus(double insulin, int minutesAgo) => new()
    {
        ["eventType"] = "Correction Bolus",
        ["insulin"] = insulin,
        ["created_at"] = DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["enteredBy"] = "loop://synthetic",
        ["syncIdentifier"] = Guid.NewGuid().ToString(),
    };

    private async Task<JsonElement[]> PostAsync(params object[] uploads)
    {
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", uploads);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
    }

    private async Task<string?> RestIdAsync(string syncIdentifier)
    {
        var served = await AuthenticatedClient.GetFromJsonAsync<JsonElement[]>("/api/v1/treatments.json?count=100");
        return served!
            .Where(t => SyncIdentifierOf(t) == syncIdentifier)
            .Select(t => t.GetProperty("_id").GetString())
            .SingleOrDefault();
    }

    [Fact]
    public async Task CreateUpdateAndDelete_CarryTheIdTheReadServes()
    {
        await ListenAsync();
        var upload = LoopBolus(0.65, minutesAgo: 20);
        var syncIdentifier = (string)upload["syncIdentifier"];

        var created = await PostAsync(upload);
        var restId = await RestIdAsync(syncIdentifier);
        restId.Should().NotBeNull();

        created.Should().ContainSingle().Which.GetProperty("_id").GetString().Should().Be(restId);
        (await WaitForAsync("create", syncIdentifier)).GetProperty("doc").GetProperty("_id").GetString()
            .Should().Be(restId);

        var patch = await AuthenticatedClient.PatchAsync(
            $"/api/v3/treatments/{restId}", JsonContent.Create(new { insulin = 0.7 }));
        patch.IsSuccessStatusCode.Should().BeTrue();
        (await WaitForAsync("update", syncIdentifier)).GetProperty("doc").GetProperty("_id").GetString()
            .Should().Be(restId);

        (await AuthenticatedClient.DeleteAsync($"/api/v1/treatments/{restId}")).IsSuccessStatusCode.Should().BeTrue();
        (await WaitForAsync("delete", syncIdentifier)).GetProperty("identifier").GetString().Should().Be(restId);
    }

    [Fact]
    public async Task MixedBatch_AnswersEveryTreatmentInOrderAndBroadcastsOnlyTheWrittenOne()
    {
        await ListenAsync();
        var deleted = LoopBolus(0.35, minutesAgo: 60);
        var deletedSync = (string)deleted["syncIdentifier"];
        await PostAsync(deleted);
        await WaitForAsync("create", deletedSync);
        var deletedId = await RestIdAsync(deletedSync);
        (await AuthenticatedClient.DeleteAsync($"/api/v1/treatments/{deletedId}")).IsSuccessStatusCode.Should().BeTrue();

        var fresh = LoopBolus(0.15, minutesAgo: 10);
        var freshSync = (string)fresh["syncIdentifier"];
        var reply = await PostAsync(deleted, fresh);

        var freshId = await RestIdAsync(freshSync);
        reply.Select(t => t.GetProperty("_id").GetString()).Should().Equal(deletedId, freshId);
        await WaitForAsync("create", freshSync);
        Events("create", deletedSync).Should().ContainSingle("the re-sent treatment the user deleted is not created again");
        (await RestIdAsync(deletedSync)).Should().BeNull();
    }

    [Fact]
    public async Task ReUpload_OfATreatmentTheUserDeleted_IsNotCreatedOrBroadcast()
    {
        await ListenAsync();
        var upload = LoopBolus(0.45, minutesAgo: 30);
        var syncIdentifier = (string)upload["syncIdentifier"];

        await PostAsync(upload);
        await WaitForAsync("create", syncIdentifier);
        var restId = await RestIdAsync(syncIdentifier);
        (await AuthenticatedClient.DeleteAsync($"/api/v1/treatments/{restId}")).IsSuccessStatusCode.Should().BeTrue();

        (await PostAsync(upload)).Should().ContainSingle(
                "Loop pairs the reply with its request by position and fails a batch whose counts differ")
            .Which.GetProperty("_id").GetString().Should().Be(restId);
        var v3 = await AuthenticatedClient.PostAsJsonAsync("/api/v3/treatments", upload);
        v3.StatusCode.Should().Be(HttpStatusCode.OK);
        var dedup = await v3.Content.ReadFromJsonAsync<JsonElement>();
        dedup.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        dedup.GetProperty("identifier").GetString().Should().Be(restId);

        // Writes are broadcast before their request returns, so once a later write has arrived, a
        // create for either re-upload would have too.
        var sentinel = LoopBolus(0.25, minutesAgo: 5);
        await PostAsync(sentinel);
        await WaitForAsync("create", (string)sentinel["syncIdentifier"]);

        Events("create", syncIdentifier).Should().ContainSingle();
        (await RestIdAsync(syncIdentifier)).Should().BeNull();
    }
}
