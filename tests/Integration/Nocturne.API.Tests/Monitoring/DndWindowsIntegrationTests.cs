using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Monitoring;

/// <summary>
/// End-to-end coverage for scoped Do Not Disturb (ADR 0004 D5) against real Postgres + RLS:
/// the <c>dnd_windows</c> endpoints (idempotency, one-per-scope supersede, resolve-on-read) and
/// the manual-DND toggle on <c>tenant-alert-settings</c> backed by a <c>scope=all</c> window.
/// </summary>
[Trait("Category", "Integration")]
public class DndWindowsIntegrationTests : AspireIntegrationTestBase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string Windows = "/api/v4/alerts/dnd/windows";
    private const string ActiveWindows = "/api/v4/alerts/dnd/windows/active";
    private const string Settings = "/api/v4/tenant-alert-settings";

    public DndWindowsIntegrationTests(AspireIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task Post_thenGetActive_returnsTheWindow()
    {
        using var client = CreateAuthenticatedClient();
        var id = Guid.NewGuid();

        var post = await client.PostAsJsonAsync(Windows, NewWindow(id, "lows"));
        post.StatusCode.Should().Be(HttpStatusCode.Created);

        var active = await GetActiveAsync(client);
        active.Should().ContainSingle(w => w.Id == id && w.Scope == "lows");
    }

    [Fact]
    public async Task Post_sameScope_supersedesTheOlderWindow()
    {
        using var client = CreateAuthenticatedClient();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await client.PostAsJsonAsync(Windows, NewWindow(first, "lows"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Windows, NewWindow(second, "lows"))).EnsureSuccessStatusCode();

        var active = await GetActiveAsync(client);
        active.Should().ContainSingle().Which.Id.Should().Be(second);
    }

    [Fact]
    public async Task Post_sameId_isIdempotent()
    {
        using var client = CreateAuthenticatedClient();
        var id = Guid.NewGuid();

        var first = await client.PostAsJsonAsync(Windows, NewWindow(id, "all"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await client.PostAsJsonAsync(Windows, NewWindow(id, "all"));
        second.StatusCode.Should().Be(HttpStatusCode.OK); // idempotent hit

        (await GetActiveAsync(client)).Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task Clear_removesTheWindowFromActive()
    {
        using var client = CreateAuthenticatedClient();
        var id = Guid.NewGuid();
        (await client.PostAsJsonAsync(Windows, NewWindow(id, "highs"))).EnsureSuccessStatusCode();

        var clear = await client.PostAsync($"{Windows}/{id}/clear", content: null);
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetActiveAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task ManualToggleOn_createsAllWindow_visibleInBothSurfaces()
    {
        using var client = CreateAuthenticatedClient();

        var put = await client.PutAsJsonAsync(Settings, new { dndManualActive = true });
        put.EnsureSuccessStatusCode();

        // The toggle is backed by a scope=all window.
        (await GetActiveAsync(client)).Should().ContainSingle().Which.Scope.Should().Be("all");

        // ...and the settings response reflects it through the unchanged DTO.
        var settings = await client.GetFromJsonAsync<SettingsDto>(Settings, Json);
        settings!.DndManualActive.Should().BeTrue();
    }

    [Fact]
    public async Task ManualToggleOff_clearsTheAllWindow()
    {
        using var client = CreateAuthenticatedClient();
        (await client.PutAsJsonAsync(Settings, new { dndManualActive = true })).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync(Settings, new { dndManualActive = false })).EnsureSuccessStatusCode();

        (await GetActiveAsync(client)).Should().BeEmpty();
        var settings = await client.GetFromJsonAsync<SettingsDto>(Settings, Json);
        settings!.DndManualActive.Should().BeFalse();
    }

    private async Task<List<WindowDto>> GetActiveAsync(HttpClient client)
    {
        var resp = await client.GetAsync(ActiveWindows);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<WindowDto>>(body, Json) ?? new();
    }

    private static object NewWindow(Guid id, string scope) => new
    {
        id,
        scope,
        startedAt = DateTime.UtcNow.AddMinutes(-1),
        endsAt = (DateTime?)null,
        source = "integration-test",
    };

    private sealed record WindowDto(Guid Id, string Scope, DateTime? ClearedAt);

    private sealed record SettingsDto(bool DndManualActive, bool DndScheduleEnabled);
}
