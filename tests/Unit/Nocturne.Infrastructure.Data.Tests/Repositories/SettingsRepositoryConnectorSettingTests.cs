using FluentAssertions;
using Nocturne.Core.Constants;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The rows a connector service keeps under <see cref="SettingsKeys.ConnectorsPrefix"/>, which the
/// v3 settings collection neither serves nor writes.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsRepositoryConnectorSettingTests : IDisposable
{
    private const string Key = SettingsKeys.ConnectorsPrefix + "demo:setting";

    private static readonly DateTimeOffset Created = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public SettingsRepositoryConnectorSettingTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private SettingsRepository Repository(Guid tenant) => new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)));

    [Fact]
    public async Task ASettingNobodyStoredReadsAsNull()
    {
        (await Repository(_tenant).GetConnectorSettingAsync(Key)).Should().BeNull();
    }

    [Fact]
    public async Task AStoredSettingReadsBackAndIsRewrittenInPlace()
    {
        var repository = Repository(_tenant);

        await repository.SetConnectorSettingAsync(Key, "{\"a\":1}", "Demo setting", Created);
        await repository.SetConnectorSettingAsync(Key, "{\"a\":2}", "not used on update", Created.AddHours(1));

        (await repository.GetConnectorSettingAsync(Key)).Should().Be("{\"a\":2}");
        await using var db = _db.CreateContext();
        var row = db.Settings.Single(s => s.Key == Key);
        row.Notes.Should().Be("Demo setting");
        row.IsActive.Should().BeTrue();
        row.SrvCreated.Should().Be(Created);
        row.SrvModified.Should().Be(Created.AddHours(1));
        row.Mills.Should().Be(Created.AddHours(1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task AnInactiveRowReadsAsNull()
    {
        await Repository(_tenant).SetConnectorSettingAsync(Key, "{}", "Demo setting", Created);
        await using (var db = _db.CreateContext())
        {
            db.Settings.Single(s => s.Key == Key).IsActive = false;
            await db.SaveChangesAsync();
        }

        (await Repository(_tenant).GetConnectorSettingAsync(Key)).Should().BeNull();
    }

    [Fact]
    public async Task ATenantReadsAndWritesOnlyItsOwnRow()
    {
        await Repository(_tenant).SetConnectorSettingAsync(Key, "mine", "Demo setting", Created);

        (await Repository(_otherTenant).GetConnectorSettingAsync(Key)).Should().BeNull();

        await Repository(_otherTenant).SetConnectorSettingAsync(Key, "theirs", "Demo setting", Created);
        (await Repository(_tenant).GetConnectorSettingAsync(Key)).Should().Be("mine");
        (await Repository(_otherTenant).GetConnectorSettingAsync(Key)).Should().Be("theirs");
    }

    [Theory]
    [InlineData("ui:settings:theme")]
    [InlineData("units")]
    [InlineData("Connectors:demo")]
    public async Task AKeyOutsideTheConnectorPrefixIsRefused(string key)
    {
        var repository = Repository(_tenant);

        await repository.Invoking(r => r.GetConnectorSettingAsync(key)).Should().ThrowAsync<ArgumentException>();
        await repository.Invoking(r => r.SetConnectorSettingAsync(key, "{}", "Demo setting", Created))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AConnectorRowIsNotADocumentOfTheV3Collection()
    {
        var repository = Repository(_tenant);
        await repository.SetConnectorSettingAsync(Key, "{}", "Demo setting", Created);

        (await repository.GetSettingsByKeyAsync(Key)).Should().BeNull();
        (await repository.GetSettingsAsync()).Should().BeEmpty();
    }
}
