using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 settings document is addressed by its <c>identifier</c> and carries whatever fields its
/// writer chose, as upstream's generic collection stores it. PUT is upstream's UPDATE: it replaces
/// the document, or inserts it when none has that identifier. AAPS keeps its <c>aaps</c> document
/// and its client-control pairing offers this way.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsUpsertTests : IDisposable
{
    private const long Date = 1792481008798;

    private readonly NocturneDbContext _context;
    private readonly SettingsController _controller;

    public SettingsUpsertTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"v3_settings_upsert_{Guid.NewGuid()}");
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _controller = new SettingsController(
            new SettingsRepository(_context),
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
        var body = JsonSerializer.SerializeToElement(created.Value);
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
    public async Task A_put_to_a_stored_identifier_replaces_the_document()
    {
        await _controller.UpdateSettings("aaps", Json(AapsDocument("""{"pump":"Dana-i"}""", extra: "\"stale\":true")));
        var srvCreated = (await Read("aaps")).GetProperty("srvCreated").GetInt64();

        var result = await _controller.UpdateSettings("aaps", Json(AapsDocument("""{"pump":"Omnipod Dash"}""")));

        var ok = result.Should().BeAssignableTo<ObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var body = JsonSerializer.SerializeToElement(ok.Value);
        body.GetProperty("status").GetInt32().Should().Be(200);
        body.GetProperty("lastModified").GetInt64().Should().BeGreaterThan(0);

        var stored = await Read("aaps");
        stored.GetProperty("runningConfig").GetProperty("pump").GetString().Should().Be("Omnipod Dash");
        stored.TryGetProperty("stale", out _).Should().BeFalse("a PUT replaces the whole document");
        stored.GetProperty("srvCreated").GetInt64().Should().Be(srvCreated);
    }

    [Fact]
    public async Task A_put_ignores_the_server_fields_its_writer_echoes_back()
    {
        var result = await _controller.UpdateSettings(
            "aaps",
            Json(AapsDocument("{}", extra: "\"srvModified\":1792481000000,\"srvCreated\":1792481000000,\"identifier\":\"other\"")));

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(201);
        (await Read("aaps")).GetProperty("identifier").GetString().Should().Be("aaps");
    }

    [Fact]
    public async Task A_put_without_a_date_is_rejected_as_upstream_rejects_it()
    {
        var result = await _controller.UpdateSettings("aaps", Json("""{"app":"AAPS","utcOffset":660}"""));

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_document_put_by_identifier_is_deleted_by_identifier()
    {
        const string offer = "aaps_clientcontrol_offer_ba1fb19e-e5d2-4c5d-92bc-4dbf683f0a88";
        await _controller.UpdateSettings(offer, Json(AapsDocument("{}")));

        var deleted = await _controller.DeleteSettings(offer);

        deleted.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
        var read = await _controller.GetSettingsById(offer);
        read.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    private async Task<JsonElement> Read(string identifier)
    {
        var result = await _controller.GetSettingsById(identifier);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static string AapsDocument(string runningConfig, string? extra = null) =>
        $$"""{"date":{{Date}},"utcOffset":660,"app":"AAPS","device":"motorola edge 50","runningConfig":{{runningConfig}}{{(extra is null ? "" : "," + extra)}}}""";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
