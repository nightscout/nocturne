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
/// The identifier a v3 treatment POST returns is the one GET, search, history, PUT, PATCH and DELETE
/// accept, as in Nightscout, where create answers with the stored identifier. AAPS keeps it as the
/// record's Nightscout id and edits and deletes by it.
/// </summary>
[Trait("Category", "Integration")]
public class V3TreatmentIdentifierIntegrationTests : ApiIntegrationTestBase
{
    public V3TreatmentIdentifierIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static long At(int minutesAgo) =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - minutesAgo * 60_000L) / 1000 * 1000;

    private static object Bolus(double insulin, long date) => new
    {
        eventType = "Correction Bolus",
        insulin,
        date,
        app = "AAPS",
        device = "AAPS-e2e",
        utcOffset = 0,
        isValid = true,
        type = "NORMAL",
    };

    private async Task<string> PostAsync(object treatment)
    {
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v3/treatments", treatment);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var identifier = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("identifier").GetString();
        MongoObjectId.IsObjectId(identifier).Should().BeTrue();
        return identifier!;
    }

    private async Task<List<JsonElement>> ResultAsync(string path)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(path);
        return body.GetProperty("result").EnumerateArray().ToList();
    }

    private static double? Insulin(JsonElement t) =>
        t.TryGetProperty("insulin", out var insulin) && insulin.ValueKind == JsonValueKind.Number ? insulin.GetDouble() : null;

    private async Task<List<string?>> SearchIdentifiersAsync(double insulin) =>
        (await ResultAsync("/api/v3/treatments?limit=100"))
            .Where(t => Insulin(t) == insulin)
            .Select(t => t.GetProperty("identifier").GetString())
            .ToList();

    [Fact]
    public async Task The_posted_identifier_reads_searches_and_pages_history()
    {
        var identifier = await PostAsync(Bolus(0.95, At(20)));

        (await AuthenticatedClient.GetAsync($"/api/v3/treatments/{identifier}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SearchIdentifiersAsync(0.95)).Should().Equal(identifier);
        (await ResultAsync("/api/v3/treatments/history/0"))
            .Where(t => Insulin(t) == 0.95)
            .Select(t => t.GetProperty("identifier").GetString())
            .Should().Equal(identifier);
        var v1 = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v1/treatments.json?count=100");
        v1.EnumerateArray().Where(t => Insulin(t) == 0.95).Select(t => t.GetProperty("_id").GetString())
            .Should().Equal(identifier);
    }

    [Fact]
    public async Task The_posted_identifier_patches_and_replaces_in_place()
    {
        var date = At(30);
        var identifier = await PostAsync(Bolus(1.15, date));

        var patch = await AuthenticatedClient.PatchAsync(
            $"/api/v3/treatments/{identifier}",
            JsonContent.Create(new { insulin = 1.2 }));
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("identifier").GetString().Should().Be(identifier);
        (await SearchIdentifiersAsync(1.15)).Should().BeEmpty();
        (await SearchIdentifiersAsync(1.2)).Should().Equal(identifier);

        var put = await AuthenticatedClient.PutAsJsonAsync($"/api/v3/treatments/{identifier}", Bolus(1.25, date));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SearchIdentifiersAsync(1.2)).Should().BeEmpty();
        (await SearchIdentifiersAsync(1.25)).Should().Equal(identifier);
    }

    [Fact]
    public async Task The_posted_identifier_deletes_and_a_re_upload_stays_deleted()
    {
        var upload = Bolus(0.85, At(70));
        var identifier = await PostAsync(upload);

        (await AuthenticatedClient.DeleteAsync($"/api/v3/treatments/{identifier}")).IsSuccessStatusCode.Should().BeTrue();
        (await AuthenticatedClient.GetAsync($"/api/v3/treatments/{identifier}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await AuthenticatedClient.PostAsJsonAsync("/api/v3/treatments", upload)).IsSuccessStatusCode.Should().BeTrue();
        (await SearchIdentifiersAsync(0.85)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_re_upload_of_the_same_event_keeps_one_record_under_one_identifier()
    {
        var upload = Bolus(0.55, At(40));

        var first = await PostAsync(upload);
        var second = await PostAsync(upload);

        second.Should().Be(first);
        (await SearchIdentifiersAsync(0.55)).Should().Equal(first);
    }

    [Fact]
    public async Task A_duplicate_post_answers_with_the_identifier_search_serves()
    {
        var identifier = await PostAsync(Bolus(0.65, At(50)));

        var duplicate = await AuthenticatedClient.PostAsJsonAsync("/api/v3/treatments", new
        {
            _id = identifier,
            eventType = "Correction Bolus",
            insulin = 0.65,
            date = At(50),
            app = "AAPS",
        });

        duplicate.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        body.GetProperty("identifier").GetString().Should().Be(identifier);
    }
}
