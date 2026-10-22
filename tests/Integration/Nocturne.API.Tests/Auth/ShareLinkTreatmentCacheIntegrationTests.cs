using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A public share link with full history reads <c>/api/v1/treatments</c> through the same
/// recent-treatments cache as the tenant's members, in the partition of its own categories.
/// Row level security hides <c>notes</c> and <c>state_spans</c> from every share, so the share and
/// the owner must never be served each other's cached list.
/// </summary>
/// <remarks>
/// The cache only serves skip 0, no find and a count of 10, 50 or 100, so a read with
/// <c>count=5</c> reaches the database every time and stands as the control for what row level
/// security lets the share see.
/// </remarks>
[Trait("Category", "Integration")]
public class ShareLinkTreatmentCacheIntegrationTests : ApiIntegrationTestBase
{
    private const string CachedRead = "/api/v1/treatments?count=10";
    private const string UncachedRead = "/api/v1/treatments?count=5";

    private readonly string _privateNote = $"private-note-{Guid.NewGuid():N}";
    private HttpClient _admin = null!;
    private HttpClient? _share;

    public ShareLinkTreatmentCacheIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (adminId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Share Admin");
        _admin = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, adminId));
    }

    public override async Task DisposeAsync()
    {
        _share?.Dispose();
        using (var disable = await _admin.DeleteAsync("/api/v4/share"))
        {
            disable.EnsureSuccessStatusCode();
        }

        _admin.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task OwnerWarmsTheCache_FullHistoryShare_IsNotServedTheOwnersHiddenTreatments()
    {
        await UploadTreatmentsAsync();
        var share = await EnableFullHistoryTreatmentShareAsync();

        var control = await GetTreatmentsAsync(share, UncachedRead);
        control.Should().Contain(t => IsBolus(t), "the share was granted treatments.read");
        control.Should().NotContain(t => IsPrivateNote(t), "row level security hides notes from every share");
        control.Should().NotContain(t => IsTemporaryTarget(t), "row level security hides state spans from every share");

        var owner = await GetTreatmentsAsync(AuthenticatedClient, CachedRead);
        owner.Should().Contain(t => IsPrivateNote(t)).And.Contain(t => IsTemporaryTarget(t));

        var shared = await GetTreatmentsAsync(share, CachedRead);
        shared.Should().Contain(t => IsBolus(t));
        shared.Should().NotContain(t => IsPrivateNote(t),
            "the owner's cached list holds a note that row level security hides from the share");
        shared.Should().NotContain(t => IsTemporaryTarget(t),
            "the owner's cached list holds a state span that row level security hides from the share");
    }

    [Fact]
    public async Task ShareWarmsTheCache_Owner_StillSeesEveryTreatment()
    {
        await UploadTreatmentsAsync();
        var share = await EnableFullHistoryTreatmentShareAsync();

        var shared = await GetTreatmentsAsync(share, CachedRead);
        shared.Should().NotContain(t => IsPrivateNote(t));

        var owner = await GetTreatmentsAsync(AuthenticatedClient, CachedRead);
        owner.Should().Contain(t => IsPrivateNote(t),
            "the owner reads every treatment, not the narrowed list a share left in the cache");
        owner.Should().Contain(t => IsTemporaryTarget(t),
            "the owner reads every treatment, not the narrowed list a share left in the cache");
    }

    private static string MinutesAgo(int minutes) =>
        DateTime.UtcNow.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private async Task UploadTreatmentsAsync()
    {
        object[] treatments =
        [
            new { eventType = "Correction Bolus", insulin = 1.5, created_at = MinutesAgo(20), enteredBy = "share-cache-test" },
            new { eventType = "Note", notes = _privateNote, created_at = MinutesAgo(15), enteredBy = "share-cache-test" },
            new
            {
                eventType = "Temporary Target",
                created_at = MinutesAgo(10),
                enteredBy = "share-cache-test",
                reason = "Activity",
                duration = 45,
                targetTop = 140,
                targetBottom = 140,
            },
        ];

        using var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", treatments);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Mints the tenant's share link, gives it full history and treatments, and returns a client
    /// addressing its host once that host serves treatments.
    /// </summary>
    private async Task<HttpClient> EnableFullHistoryTreatmentShareAsync()
    {
        using var rotate = await _admin.PostAsync("/api/v4/share/rotate", null);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK, await rotate.Content.ReadAsStringAsync());
        var url = (await rotate.Content.ReadFromJsonAsync<JsonObject>())!["url"]!.GetValue<string>();

        using (var history = await _admin.PutAsJsonAsync("/api/v4/share/full-history", new { fullHistory = true }))
        {
            history.StatusCode.Should().Be(HttpStatusCode.OK, await history.Content.ReadAsStringAsync());
        }

        using (var scopes = await _admin.PutAsJsonAsync(
            "/api/v4/share/scopes", new { scopes = new[] { "glucose.read", "treatments.read" } }))
        {
            scopes.StatusCode.Should().Be(HttpStatusCode.OK, await scopes.Content.ReadAsStringAsync());
        }

        _share = Fixture.CreateApexClient();
        _share.DefaultRequestHeaders.Host = new Uri(url).Authority;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using var probe = await _share.GetAsync(UncachedRead);
            if (probe.StatusCode == HttpStatusCode.OK)
                return _share;

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"The share host never served treatments: {(int)probe.StatusCode} "
                    + await probe.Content.ReadAsStringAsync());
            }

            await Task.Delay(250);
        }
    }

    private static async Task<List<JsonObject>> GetTreatmentsAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray().Select(node => node!.AsObject()).ToList();
    }

    private static string? Field(JsonObject treatment, string name) =>
        treatment.TryGetPropertyValue(name, out var value) && value is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;

    private static bool IsBolus(JsonObject treatment) =>
        Field(treatment, "eventType") == "Correction Bolus";

    private bool IsPrivateNote(JsonObject treatment) =>
        Field(treatment, "notes") == _privateNote;

    private static bool IsTemporaryTarget(JsonObject treatment) =>
        Field(treatment, "eventType") == "Temporary Target";
}
