using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// The rows Nocturne owns (UI settings, connector settings) share the settings table with the v3
/// settings collection but are not in it: they have their own read and write scopes, which the v3
/// collection's <c>therapy.read</c> / <c>therapy.readwrite</c> must not bypass.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsUiRowsTests : IDisposable
{
    private const string AlarmsKey = "ui:settings:notifications:alarms";
    private const string ConnectorKey = "connectors:myfitnesspal:matching-settings";
    private static readonly Guid AlarmsId = Guid.CreateVersion7();

    private readonly NocturneDbContext _context;
    private readonly SettingsRepository _repository;
    private readonly SettingsController _controller;

    public SettingsUiRowsTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"v3_settings_ui_rows_{Guid.NewGuid()}");
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _context.Settings.Add(new SettingsEntity
        {
            Id = AlarmsId,
            Key = AlarmsKey,
            Value = """{"emergencyContacts":[{"phone":"+1 555 0100"}],"channels":{"pushover":{"apiToken":"a-secret"}}}""",
        });
        _context.Settings.Add(new SettingsEntity { Id = Guid.CreateVersion7(), Key = "ui:settings:complete", Value = "{}" });
        _context.Settings.Add(new SettingsEntity { Id = Guid.CreateVersion7(), Key = ConnectorKey, Value = "{}" });
        _context.Settings.Add(new SettingsEntity { Id = Guid.CreateVersion7(), Key = "aaps", Value = "{}" });
        _context.SaveChanges();
        _repository = new SettingsRepository(_context.AsFactory());
        _controller = new SettingsController(
            _repository,
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
    public async Task No_read_of_the_collection_returns_a_ui_settings_row()
    {
        (await _repository.GetSettingsAsync()).Select(s => s.Key).Should().Equal("aaps");
        (await _repository.GetSettingsWithAdvancedFilterAsync(count: 100)).Select(s => s.Key).Should().Equal("aaps");
        (await _repository.GetSettingsWithAdvancedFilterAsync(findQuery: "secret")).Should().BeEmpty();
        (await _repository.CountSettingsAsync()).Should().Be(1);
        (await _repository.GetSettingsByIdAsync(AlarmsKey)).Should().BeNull();
        (await _repository.GetSettingsByIdAsync(AlarmsId.ToString())).Should().BeNull();
        (await _repository.GetSettingsByIdAsync(ConnectorKey)).Should().BeNull();
        (await _repository.GetSettingsByKeyAsync(AlarmsKey)).Should().BeNull();

        var byId = await _controller.GetSettingsById(AlarmsKey);
        byId.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task No_write_through_the_collection_reaches_a_ui_settings_row()
    {
        (await _repository.DeleteSettingsAsync(AlarmsKey)).Should().BeFalse();
        (await _repository.UpdateSettingsAsync(AlarmsKey, new Settings { Key = "aaps" })).Should().BeNull();
        await FluentActions.Awaiting(() => _repository.CreateSettingsAsync([new Settings { Key = AlarmsKey }]))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => _repository.PutSettingsAsync(AlarmsKey, new Settings()))
            .Should().ThrowAsync<ArgumentException>();

        var put = await _controller.UpdateSettings(AlarmsKey, JsonDocument.Parse("{}").RootElement.Clone());
        put.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
        await FluentActions.Awaiting(() => _repository.UpdateSettingsAsync("aaps", new Settings { Key = ConnectorKey }))
            .Should().ThrowAsync<ArgumentException>();

        var post = await _controller.CreateSettings(
            JsonDocument.Parse($$"""{"key":"aaps2","_id":"{{AlarmsId}}"}""").RootElement.Clone());
        post.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);

        _context.ChangeTracker.Clear();
        _context.Settings.Single(s => s.Key == AlarmsKey).Value.Should().Contain("a-secret");
    }
}
