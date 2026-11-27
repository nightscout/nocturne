using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 settings document is addressed by its <c>identifier</c> and carries whatever fields its
/// writer chose, as upstream's generic collection stores it. PUT is upstream's UPDATE: it replaces
/// the document, or inserts it when none has that identifier. AAPS keeps its <c>aaps</c> document
/// and its client-control pairing offers this way, and reads them back from a v3 READ's
/// <c>result</c>.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsUpsertTests : IDisposable
{
    private const long Date = 1792481008798;
    private const long EchoedServerTime = 1700000000000;

    private static readonly JsonSerializerOptions Wire = NightscoutJsonOptions.Create();

    private readonly NocturneDbContext _context;
    private readonly SettingsController _controller;

    public SettingsUpsertTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"v3_settings_upsert_{Guid.NewGuid()}");
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _controller = new SettingsController(
            new SettingsRepository(_context.AsFactory()),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<SettingsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_put_to_an_unknown_identifier_inserts_the_document()
    {
        var result = await _controller.UpdateSettings("aaps", Json(AapsDocument("""{"pump":"Dana-i"}""")));

        var created = result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        var body = Serialize(created.Value);
        body.GetProperty("status").GetInt32().Should().Be(201);
        body.GetProperty("identifier").GetString().Should().Be("aaps");
        body.GetProperty("lastModified").GetInt64().Should().BeGreaterThan(0);

        var stored = await Read("aaps");
        stored.GetProperty("identifier").GetString().Should().Be("aaps");
        stored.GetProperty("app").GetString().Should().Be("AAPS");
        stored.GetProperty("date").GetInt64().Should().Be(Date);
        stored.GetProperty("runningConfig").GetProperty("pump").GetString().Should().Be("Dana-i");
        stored.GetProperty("srvModified").GetInt64()
            .Should().BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 60_000,
                "AAPS reads srvModified as Unix milliseconds, as upstream writes it");
    }

    [Fact]
    public async Task A_put_to_a_stored_identifier_replaces_the_document_and_keeps_its_creation_time()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        _context.Settings.Add(new SettingsEntity
        {
            Id = Guid.CreateVersion7(),
            Key = "aaps",
            SrvCreated = created,
            SrvModified = created,
            AdditionalPropertiesJson = """{"stale":true}""",
        });
        await _context.SaveChangesAsync();

        var result = await _controller.UpdateSettings("aaps", Json(AapsDocument("""{"pump":"Omnipod Dash"}""")));

        var ok = result.Should().BeAssignableTo<ObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var body = Serialize(ok.Value);
        body.GetProperty("status").GetInt32().Should().Be(200);
        body.GetProperty("lastModified").GetInt64().Should().BeGreaterThan(0);

        var stored = await Read("aaps");
        stored.GetProperty("runningConfig").GetProperty("pump").GetString().Should().Be("Omnipod Dash");
        stored.TryGetProperty("stale", out _).Should().BeFalse("a PUT replaces the whole document");
        stored.GetProperty("srvCreated").GetInt64().Should().Be(created.ToUnixTimeMilliseconds());
        _context.Settings.Should().ContainSingle();
    }

    [Fact]
    public async Task A_put_sets_the_server_fields_itself_rather_than_taking_the_echoed_ones()
    {
        var result = await _controller.UpdateSettings(
            "aaps",
            Json(AapsDocument(
                "{}",
                extra: $"\"srvModified\":{EchoedServerTime},\"srvCreated\":{EchoedServerTime},\"identifier\":\"other\",\"key\":\"other\"")));

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(201);
        var stored = await Read("aaps");
        stored.GetProperty("identifier").GetString().Should().Be("aaps");
        stored.GetProperty("srvModified").GetInt64().Should().BeGreaterThan(EchoedServerTime);
        stored.GetProperty("srvCreated").GetInt64().Should().BeGreaterThan(EchoedServerTime);
    }

    [Fact]
    public async Task A_put_cannot_address_another_row_through_a_body_id_in_any_case()
    {
        var other = Guid.CreateVersion7();
        _context.Settings.Add(new SettingsEntity { Id = other, Key = "displayUnits", Value = "\"mmol\"" });
        await _context.SaveChangesAsync();

        await _controller.UpdateSettings("newkey", Json(AapsDocument("{}", extra: $"\"_ID\":\"{other}\"")));

        var untouched = await _context.Settings.SingleOrDefaultAsync(s => s.Id == other);
        untouched!.Key.Should().Be("displayUnits");
        untouched.Value.Should().Be("\"mmol\"");
        _context.Settings.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("""{"app":"AAPS","utcOffset":660}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"aaps\"")]
    public async Task A_put_of_anything_but_a_dated_document_is_rejected_as_upstream_rejects_it(string body)
    {
        var result = await _controller.UpdateSettings("aaps", Json(body));

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_document_put_by_identifier_is_found_by_search_and_deleted_by_identifier()
    {
        const string offer = "aaps_clientcontrol_offer_ba1fb19e-e5d2-4c5d-92bc-4dbf683f0a88";
        await _controller.UpdateSettings(offer, Json(AapsDocument("{}", extra: "\"offer\":\"wrapped\"")));

        var search = Serialize(((ObjectResult)await _controller.GetSettings()).Value);
        var found = search.GetProperty("result").EnumerateArray()
            .Single(d => d.GetProperty("identifier").GetString() == offer);
        found.GetProperty("offer").GetString().Should().Be("wrapped");

        var deleted = await _controller.DeleteSettings(offer);

        deleted.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
        var read = await _controller.GetSettingsById(offer);
        read.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task A_search_dates_its_answer_by_the_newest_srvModified()
    {
        await _controller.UpdateSettings("aaps", Json(AapsDocument("{}")));
        var srvModified = (await Read("aaps")).GetProperty("srvModified").GetInt64();

        await _controller.GetSettings();

        _controller.Response.Headers.LastModified.ToString()
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(srvModified).UtcDateTime.ToString("R"));
    }

    /// <summary>The document a v3 READ answers, from its <c>{status, result}</c> envelope.</summary>
    private async Task<JsonElement> Read(string identifier)
    {
        var result = await _controller.GetSettingsById(identifier);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = Serialize(ok.Value);
        body.GetProperty("status").GetInt32().Should().Be(200);
        return body.GetProperty("result");
    }

    private static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(value, Wire);

    private static string AapsDocument(string runningConfig, string? extra = null) =>
        $$"""{"date":{{Date}},"utcOffset":660,"app":"AAPS","device":"motorola edge 50","runningConfig":{{runningConfig}}{{(extra is null ? "" : "," + extra)}}}""";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
