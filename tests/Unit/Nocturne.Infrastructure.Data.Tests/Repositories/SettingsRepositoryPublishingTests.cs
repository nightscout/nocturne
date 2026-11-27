using FluentAssertions;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Every committed write of the v3 settings collection publishes the documents it created, updated or
/// deleted; a connector setting is not in the collection and publishes nothing.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsRepositoryPublishingTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Command = "aaps_clientcontrol_wizard_c1";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly RecordingDocumentChanges<Settings> _changes = new();
    private readonly SettingsRepository _settings;

    public SettingsRepositoryPublishingTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _settings = new SettingsRepository(_context.AsFactory(), _changes);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ACreate_PublishesANewKeyAsCreated_AndAnExistingKeyAsUpdated()
    {
        await _settings.CreateSettingsAsync([new Settings { Key = "units", Value = "mg/dl" }]);
        _changes.Clear();

        await _settings.CreateSettingsAsync(
        [
            new Settings { Key = "units", Value = "mmol" },
            new Settings { Key = "language", Value = "en" },
        ]);

        _changes.Created.Should().ContainSingle().Which.Key.Should().Be("language");
        _changes.Updated.Should().ContainSingle().Which.Key.Should().Be("units");
        _changes.Batches.Should().ContainSingle("one write is one change");
    }

    [Fact]
    public async Task APut_PublishesAnInsertAsCreated_AndAReplaceAsUpdated()
    {
        (await _settings.PutSettingsAsync(Command, new Settings { Value = "1" })).Should().BeTrue();
        _changes.Created.Should().ContainSingle().Which.Key.Should().Be(Command);
        _changes.Clear();

        (await _settings.PutSettingsAsync(Command, new Settings { Value = "2" })).Should().BeFalse();

        _changes.Updated.Should().ContainSingle().Which.Key.Should().Be(Command);
        _changes.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUpdate_PublishesTheUpdatedDocument()
    {
        await _settings.CreateSettingsAsync([new Settings { Key = "units", Value = "mg/dl" }]);
        _changes.Clear();

        await _settings.UpdateSettingsAsync("units", new Settings { Key = "units", Value = "mmol" });

        _changes.Updated.Should().ContainSingle().Which.Key.Should().Be("units");
    }

    [Fact]
    public async Task ADelete_PublishesTheDeletedDocument_AndADeleteOfNothingPublishesNothing()
    {
        (await _settings.DeleteSettingsAsync(Command)).Should().BeFalse();
        _changes.Batches.Should().BeEmpty();

        await _settings.PutSettingsAsync(Command, new Settings { Value = "1" });
        _changes.Clear();

        (await _settings.DeleteSettingsAsync(Command)).Should().BeTrue();

        _changes.Deleted.Should().ContainSingle().Which.Key.Should().Be(Command);
    }

    [Fact]
    public async Task AConnectorSetting_PublishesNothing()
    {
        await _settings.SetConnectorSettingAsync(
            SettingsKeys.ConnectorsPrefix + "dexcom", "{}", "dexcom", DateTimeOffset.UtcNow);
        await _settings.SetConnectorSettingAsync(
            SettingsKeys.ConnectorsPrefix + "dexcom", "{\"a\":1}", "dexcom", DateTimeOffset.UtcNow);

        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedWriteUnderAReservedKey_PublishesNothing()
    {
        var create = () => _settings.CreateSettingsAsync([new Settings { Key = SettingsKeys.UiSettingsPrefix + "theme" }]);

        await create.Should().ThrowAsync<ArgumentException>();
        _changes.Batches.Should().BeEmpty();
    }
}
