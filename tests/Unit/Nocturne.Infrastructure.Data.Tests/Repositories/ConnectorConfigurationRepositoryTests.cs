using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="ConnectorConfigurationRepository"/> over SQLite: each write changes only the columns
/// its intent names, and the cursor and low-water-mark writes take their provider-neutral
/// read-modify-write path. The jsonb statements and the tenant pin are the Postgres tests' concern.
/// </summary>
public class ConnectorConfigurationRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly FakeTimeProvider _time = new(Now);

    public ConnectorConfigurationRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "home").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private ConnectorConfigurationRepository Configurations(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)), _time);

    private ConnectorConfigurationEntity Row(string connectorName, Guid? tenant = null)
    {
        using var db = _db.CreateContext(tenant ?? _tenant);
        return db.ConnectorConfigurations.AsNoTracking().Single(c => c.ConnectorName == connectorName);
    }

    private void Seed(string connectorName, Guid? tenant = null, string? syncCursors = null, string? marks = null)
    {
        using var db = _db.CreateContext(tenant ?? _tenant);
        db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant ?? _tenant,
            ConnectorName = connectorName,
            ConfigurationJson = """{"url":"https://example.com"}""",
            SecretsJson = """{"apiSecret":"sealed"}""",
            SyncCursorsJson = syncCursors,
            BackfillLowWaterMarks = marks,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task AConfigurationIsFoundByAnySpellingOfItsNameAndOnlyInItsTenant()
    {
        Seed("dexcom");
        Seed("glooko", _otherTenant);

        var found = await Configurations().FindAsync("Dexcom", CancellationToken.None);

        found.Should().NotBeNull();
        found!.TenantId.Should().Be(_tenant);
        found.ConnectorName.Should().Be("dexcom");
        found.ConfigurationJson.Should().Be("""{"url":"https://example.com"}""");
        found.SecretsJson.Should().Be("""{"apiSecret":"sealed"}""");
        (await Configurations().FindAsync("glooko", CancellationToken.None)).Should().BeNull();
        (await Configurations().FindAsync("libre", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheListsReadTheScopesTenantOrThePinnedOneByName()
    {
        Seed("glooko");
        Seed("dexcom");
        Seed("tidepool", _otherTenant);

        (await Configurations().ListAsync(CancellationToken.None)).Select(c => c.ConnectorName)
            .Should().Equal("dexcom", "glooko");
        (await Configurations().ListForTenantAsync(_otherTenant, CancellationToken.None)).Select(c => c.ConnectorName)
            .Should().Equal("tidepool");
        (await Configurations().AnyAsync(CancellationToken.None)).Should().BeTrue();
        (await Configurations(Guid.CreateVersion7()).AnyAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SavingAConfigurationCreatesTheRowWithoutSecretsAndLaterKeepsTheSecrets()
    {
        var created = await Configurations().SaveConfigurationAsync("Nightscout", """{"url":"a"}""", "rhys", CancellationToken.None);

        created.ConnectorName.Should().Be("nightscout");
        created.TenantId.Should().Be(_tenant);
        created.SecretsJson.Should().Be("{}");
        created.ModifiedBy.Should().Be("rhys");

        await Configurations().SaveSecretsAsync("nightscout", """{"apiSecret":"x"}""", "rhys", CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        var updated = await Configurations().SaveConfigurationAsync("nightscout", """{"url":"b"}""", "erik", CancellationToken.None);

        updated.ConfigurationJson.Should().Be("""{"url":"b"}""");
        updated.SecretsJson.Should().Be("""{"apiSecret":"x"}""");
        updated.ModifiedBy.Should().Be("erik");
        updated.LastModified.Should().Be(Now.AddHours(1));
        Row("nightscout").ConfigurationJson.Should().Be("""{"url":"b"}""");
    }

    [Fact]
    public async Task SavingSecretsCreatesTheRowWithAnEmptyConfigurationAndLaterKeepsTheConfiguration()
    {
        var created = await Configurations().SaveSecretsAsync("glooko", """{"password":"p"}""", null, CancellationToken.None);
        created.ConfigurationJson.Should().Be("{}");
        created.SecretsJson.Should().Be("""{"password":"p"}""");

        Seed("dexcom");
        var updated = await Configurations().SaveSecretsAsync("dexcom", """{"password":"q"}""", "rhys", CancellationToken.None);

        updated.ConfigurationJson.Should().Be("""{"url":"https://example.com"}""");
        Row("dexcom").SecretsJson.Should().Be("""{"password":"q"}""");
    }

    [Fact]
    public async Task SettingActiveReplacesTheEnabledKeyInAnySpellingAndKeepsTheOthers()
    {
        using (var db = _db.CreateContext())
        {
            db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _tenant, ConnectorName = "dexcom",
                ConfigurationJson = """{"Enabled":true,"region":"us"}""",
            });
            db.SaveChanges();
        }

        var updated = await Configurations().SetActiveAsync("dexcom", false, "rhys", CancellationToken.None);
        var created = await Configurations().SetActiveAsync("libre", true, null, CancellationToken.None);

        using var config = JsonDocument.Parse(updated.ConfigurationJson);
        config.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("region", "enabled");
        config.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
        config.RootElement.GetProperty("region").GetString().Should().Be("us");
        created.ConfigurationJson.Should().Be("""{"enabled":true}""");
        created.SecretsJson.Should().Be("{}");
    }

    [Theory]
    [InlineData("""{"region":"us"}""")]
    [InlineData("""{"region":"us","enabled":true}""")]
    [InlineData("""{"region":"us","Enabled":true}""")]
    public async Task AConfigurationSaveAfterADisableKeepsTheConnectorDisabled(string saved)
    {
        await Configurations().SaveConfigurationAsync("glooko", """{"enabled":true,"region":"eu"}""", "owner", CancellationToken.None);
        await Configurations().SetActiveAsync("glooko", false, "rhys", CancellationToken.None);

        var record = await Configurations().SaveConfigurationAsync("glooko", saved, "erik", CancellationToken.None);

        var config = JsonNode.Parse(record.ConfigurationJson)!.AsObject();
        config.ToDictionary(p => p.Key, p => p.Value!.ToJsonString()).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
        record.ModifiedBy.Should().Be("erik");
        Row("glooko").ConfigurationJson.Should().Be(record.ConfigurationJson);
    }

    [Fact]
    public async Task AConfigurationSaveKeepsTheStoredFlagWhateverCaseItIsStoredIn()
    {
        await Configurations().SaveConfigurationAsync("glooko", """{"Enabled":false,"region":"eu"}""", "owner", CancellationToken.None);

        var record = await Configurations().SaveConfigurationAsync("glooko", """{"region":"us"}""", "erik", CancellationToken.None);

        JsonNode.Parse(record.ConfigurationJson)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToJsonString())
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("Enabled")]
    public async Task AConfigurationSaveOntoAStoredRowThatHasNoFlagTakesTheSavedFlag(string spelling)
    {
        Seed("dexcom");

        var record = await Configurations().SaveConfigurationAsync(
            "dexcom", $$"""{"{{spelling}}":false,"url":"b"}""", "erik", CancellationToken.None);

        record.ConfigurationJson.Should().Be("""{"url":"b","enabled":false}""");
        record.SecretsJson.Should().Be("""{"apiSecret":"sealed"}""");
    }

    [Fact]
    public async Task AConfigurationSaveOntoAStoredRowThatHasNoFlagLeavesItWithNoneWhenTheSavedHasNone()
    {
        Seed("dexcom");

        var record = await Configurations().SaveConfigurationAsync("dexcom", """{"url":"b"}""", "erik", CancellationToken.None);

        record.ConfigurationJson.Should().Be("""{"url":"b"}""");
    }

    [Fact]
    public async Task TheFirstConfigurationSaveStoresTheFlagItCarriesSpelledEnabled()
    {
        var record = await Configurations().SaveConfigurationAsync("glooko", """{"Enabled":false,"region":"us"}""", "erik", CancellationToken.None);

        JsonNode.Parse(record.ConfigurationJson)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToJsonString())
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
    }

    [Fact]
    public async Task DeletingReturnsTheDeletedRowOrNull()
    {
        Seed("dexcom");

        var deleted = await Configurations().DeleteAsync("DEXCOM", CancellationToken.None);

        deleted!.ConnectorName.Should().Be("dexcom");
        deleted.TenantId.Should().Be(_tenant);
        (await Configurations().FindAsync("dexcom", CancellationToken.None)).Should().BeNull();
        (await Configurations().DeleteAsync("dexcom", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AHealthReportWritesOnlyTheFieldsItCarriesAndClearsWhatItFlags()
    {
        Seed("dexcom");
        var attempt = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);

        var failed = await Configurations().RecordHealthAsync("dexcom",
            new ConnectorHealthUpdate(LastSyncAttempt: attempt, LastErrorMessage: "boom", LastErrorAt: attempt, IsHealthy: false),
            CancellationToken.None);

        failed!.IsHealthy.Should().BeFalse();
        failed.LastErrorMessage.Should().Be("boom");
        failed.LastErrorAt.Should().Be(attempt);
        failed.LastSuccessfulSync.Should().BeNull();

        var recovered = await Configurations().RecordHealthAsync("dexcom",
            new ConnectorHealthUpdate(LastSuccessfulSync: attempt.AddMinutes(5), ClearLastErrorMessage: true, ClearLastErrorAt: true, IsHealthy: true),
            CancellationToken.None);

        recovered!.IsHealthy.Should().BeTrue();
        recovered.LastErrorMessage.Should().BeNull();
        recovered.LastErrorAt.Should().BeNull();
        recovered.LastSyncAttempt.Should().Be(attempt);
        recovered.LastSuccessfulSync.Should().Be(attempt.AddMinutes(5));
        Row("dexcom").ConfigurationJson.Should().Be("""{"url":"https://example.com"}""");
        (await Configurations().RecordHealthAsync("libre", new ConnectorHealthUpdate(IsHealthy: true), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task AnOverLongErrorIsCutToTheColumnOutsideASurrogatePairAndMarked()
    {
        Seed("dexcom");
        var message = new string('x', ConnectorConfigurationRecord.LastErrorMessageMaxLength - 16) + "\U0001F600" + new string('y', 50);

        var stored = await Configurations().RecordHealthAsync("dexcom",
            new ConnectorHealthUpdate(LastErrorMessage: message), CancellationToken.None);

        stored!.LastErrorMessage!.Length.Should().BeLessThanOrEqualTo(ConnectorConfigurationRecord.LastErrorMessageMaxLength);
        stored.LastErrorMessage.Should().EndWith("... (truncated)");
        char.IsHighSurrogate(stored.LastErrorMessage[^("... (truncated)".Length + 1)]).Should().BeFalse();
    }

    [Fact]
    public async Task ACursorIsStoredUnderItsResourceBesideTheOthersAndRemovedAlone()
    {
        Seed("glooko", syncCursors: """{"health":{"LastGuid":"kept"}}""");

        (await Configurations().SetSyncCursorAsync("Glooko", "cgm", JsonNode.Parse("""{"LastGuid":"g1"}""")!, CancellationToken.None))
            .Should().BeTrue();

        (await Configurations().GetSyncCursorAsync("glooko", "cgm", CancellationToken.None))!["LastGuid"]!.GetValue<string>()
            .Should().Be("g1");
        (await Configurations().GetSyncCursorAsync("glooko", "health", CancellationToken.None))!["LastGuid"]!.GetValue<string>()
            .Should().Be("kept");

        (await Configurations().RemoveSyncCursorAsync("glooko", "cgm", CancellationToken.None)).Should().BeTrue();

        (await Configurations().GetSyncCursorAsync("glooko", "cgm", CancellationToken.None)).Should().BeNull();
        JsonNode.Parse(Row("glooko").SyncCursorsJson!)!.AsObject().Select(p => p.Key).Should().Equal("health");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task CursorsThatAreNotAnObjectReadAsEmptyAndAreReplacedOnWrite(string? stored)
    {
        Seed("glooko", syncCursors: stored);

        (await Configurations().GetSyncCursorAsync("glooko", "cgm", CancellationToken.None)).Should().BeNull();
        await Configurations().SetSyncCursorAsync("glooko", "cgm", JsonValue.Create(7), CancellationToken.None);

        Row("glooko").SyncCursorsJson.Should().Be("""{"cgm":7}""");
    }

    [Fact]
    public async Task ACursorWriteForAConnectorWithNoConfigurationReportsIt()
    {
        Seed("glooko", _otherTenant);

        (await Configurations().SetSyncCursorAsync("glooko", "cgm", JsonValue.Create(1), CancellationToken.None)).Should().BeFalse();
        (await Configurations().RemoveSyncCursorAsync("glooko", "cgm", CancellationToken.None)).Should().BeFalse();
        (await Configurations().GetSyncCursorAsync("glooko", "cgm", CancellationToken.None)).Should().BeNull();
        Row("glooko", _otherTenant).SyncCursorsJson.Should().BeNull();
    }

    [Fact]
    public async Task ALowWaterMarkIsKeptPerCollectionThroughTheConnectorSourceName()
    {
        Seed("nightscout");
        var glucose = new DateTime(2024, 6, 1, 12, 30, 15, DateTimeKind.Utc);
        var treatments = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        (await Configurations().SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", glucose, CancellationToken.None))
            .Should().BeTrue();
        await Configurations().SetBackfillLowWaterMarkAsync("Nightscout", "Treatments", treatments, CancellationToken.None);

        var read = await Configurations().GetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", CancellationToken.None);
        read.Should().Be(glucose);
        read!.Value.Kind.Should().Be(DateTimeKind.Utc);
        (await Configurations().GetBackfillLowWaterMarkAsync("nightscout", "Treatments", CancellationToken.None)).Should().Be(treatments);

        await Configurations().SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", null, CancellationToken.None);
        (await Configurations().GetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", CancellationToken.None)).Should().BeNull();
        (await Configurations().GetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments", CancellationToken.None)).Should().Be(treatments);

        await Configurations().SetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments", null, CancellationToken.None);
        Row("nightscout").BackfillLowWaterMarks.Should().BeNull("the last mark removed leaves no map");
    }

    [Fact]
    public async Task ALowWaterMarkForASourceWithNoConfigurationReportsIt()
    {
        (await Configurations().SetBackfillLowWaterMarkAsync("dexcom-connector", "Glucose", DateTime.UtcNow, CancellationToken.None))
            .Should().BeFalse();
        (await Configurations().GetBackfillLowWaterMarkAsync("dexcom-connector", "Glucose", CancellationToken.None)).Should().BeNull();
    }
}
