using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="ConnectorConfigurationRepository"/> and <see cref="ConnectorResetJobRepository"/> on
/// PostgreSQL under the runtime role and Row Level Security: the jsonb-path statements that write
/// one cursor, one low-water mark or the enabled flag beside the others, and the tenant pin, which
/// reads the tenant it names and not the scope's.
/// </summary>
[Trait("Category", "Integration")]
public class ConnectorConfigurationRepositoryPostgresTests(ConnectorConfigurationRepositoryPostgresTests.Database database)
    : IClassFixture<ConnectorConfigurationRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("connector_config_repo");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>A scope that reads as <paramref name="scope"/>; a tenant pin reads as the tenant it names.</summary>
    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : INocturneDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create(scope));

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private ConnectorConfigurationRepository Configurations(Guid scope) => new(new Factory(Context, scope));

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'connector-config-repo-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"connector-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    [Fact]
    public async Task TenantPinned_TheListReadsThePinnedTenantAndTheScopeSeesOnlyItsOwn()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("dexcom", "{}", null, CancellationToken.None);
        await Configurations(b).SaveConfigurationAsync("glooko", "{}", null, CancellationToken.None);

        var fromA = Configurations(a);

        (await fromA.ListAsync(CancellationToken.None)).Select(c => c.ConnectorName).Should().Equal("dexcom");
        (await fromA.ListForTenantAsync(b, CancellationToken.None)).Should().ContainSingle()
            .Which.Should().Match<ConnectorConfigurationRecord>(c => c.ConnectorName == "glooko" && c.TenantId == b);
        (await fromA.FindAsync("glooko", CancellationToken.None)).Should().BeNull("RLS hides another tenant's row from the scope");
        (await Configurations(Guid.Empty).AnyAsync(CancellationToken.None)).Should().BeFalse("a scope that reads as no one sees no row");
    }

    [Fact]
    public async Task TheCursorStatementsWriteOneKeyBesideTheOthersAndOnlyInTheScopesTenant()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("glooko", "{}", null, CancellationToken.None);
        await Configurations(b).SaveConfigurationAsync("glooko", "{}", null, CancellationToken.None);
        var configurations = Configurations(a);

        (await configurations.SetSyncCursorAsync("Glooko", "cgm", JsonNode.Parse("""{"LastGuid":"g1"}""")!, CancellationToken.None))
            .Should().BeTrue();
        await configurations.SetSyncCursorAsync("glooko", "health", JsonValue.Create("h"), CancellationToken.None);
        await configurations.SetSyncCursorAsync("glooko", "cgm", JsonNode.Parse("""{"LastGuid":"g2"}""")!, CancellationToken.None);

        (await configurations.GetSyncCursorAsync("glooko", "cgm", CancellationToken.None))!["LastGuid"]!.GetValue<string>()
            .Should().Be("g2");
        (await configurations.GetSyncCursorAsync("glooko", "health", CancellationToken.None))!.GetValue<string>().Should().Be("h");

        (await configurations.RemoveSyncCursorAsync("glooko", "cgm", CancellationToken.None)).Should().BeTrue();

        JsonNode.Parse((await configurations.FindAsync("glooko", CancellationToken.None))!.SyncCursorsJson!)!
            .AsObject().Select(p => p.Key).Should().Equal("health");
        (await Configurations(b).FindAsync("glooko", CancellationToken.None))!.SyncCursorsJson.Should().BeNull();
        (await configurations.SetSyncCursorAsync("libre", "cgm", JsonValue.Create(1), CancellationToken.None)).Should().BeFalse();
        (await configurations.RemoveSyncCursorAsync("libre", "cgm", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ACursorWriteOverStoredCursorsThatAreNotAnObjectStartsAFreshObject()
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("glooko", "{}", null, CancellationToken.None);
        await using (var db = Context(a))
            await db.Database.ExecuteSqlAsync($"UPDATE connector_configurations SET sync_cursors = '[1]'::jsonb WHERE tenant_id = {a}");

        await Configurations(a).SetSyncCursorAsync("glooko", "cgm", JsonValue.Create(7), CancellationToken.None);

        JsonNode.Parse((await Configurations(a).FindAsync("glooko", CancellationToken.None))!.SyncCursorsJson!)!
            .ToJsonString().Should().Be("""{"cgm":7}""");
    }

    [Fact]
    public async Task TheLowWaterMarkStatementsKeepEachCollectionAndLeaveNoMapWhenTheLastGoes()
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("nightscout", "{}", null, CancellationToken.None);
        var configurations = Configurations(a);
        var glucose = new DateTime(2024, 6, 1, 12, 30, 15, 123, DateTimeKind.Utc);
        var treatments = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        (await configurations.SetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", glucose, CancellationToken.None))
            .Should().BeTrue();
        await configurations.SetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments", treatments, CancellationToken.None);

        var read = await configurations.GetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", CancellationToken.None);
        read.Should().Be(glucose);
        read!.Value.Kind.Should().Be(DateTimeKind.Utc);

        await configurations.SetBackfillLowWaterMarkAsync("nightscout", "Glucose", null, CancellationToken.None);
        (await configurations.GetBackfillLowWaterMarkAsync("nightscout-connector", "Glucose", CancellationToken.None)).Should().BeNull();
        (await configurations.GetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments", CancellationToken.None)).Should().Be(treatments);

        await configurations.SetBackfillLowWaterMarkAsync("nightscout-connector", "Treatments", null, CancellationToken.None);
        (await configurations.FindAsync("nightscout", CancellationToken.None))!.BackfillLowWaterMarks.Should().BeNull();
        (await configurations.SetBackfillLowWaterMarkAsync("dexcom-connector", "Glucose", glucose, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TheToggleWritesOnlyTheEnabledKeyStampsTheRowAndStaysInTheScopesTenant()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero));
        var configurations = new ConnectorConfigurationRepository(new Factory(Context, a), time);
        await configurations.SaveConfigurationAsync(
            "glooko", """{"Enabled":true,"region":"eu","retries":3}""", "owner", CancellationToken.None);
        await configurations.SaveSecretsAsync("glooko", """{"password":"x"}""", "owner", CancellationToken.None);
        await Configurations(b).SaveConfigurationAsync("glooko", """{"enabled":true}""", "owner", CancellationToken.None);

        var toggled = await configurations.SetActiveAsync("Glooko", false, "rhys", CancellationToken.None);

        toggled.Should().Match<ConnectorConfigurationRecord>(c =>
            c.TenantId == a && c.ConnectorName == "glooko" && c.ModifiedBy == "rhys" && c.LastModified == time.GetUtcNow());
        var config = JsonNode.Parse(toggled.ConfigurationJson)!.AsObject();
        config.ToDictionary(p => p.Key, p => p.Value!.ToJsonString()).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"eu\"", ["retries"] = "3" });
        JsonNode.Parse(toggled.SecretsJson)!["password"]!.GetValue<string>().Should().Be("x");
        JsonNode.Parse((await Configurations(b).FindAsync("glooko", CancellationToken.None))!.ConfigurationJson)!["enabled"]!
            .GetValue<bool>().Should().BeTrue();

        var unattributed = await configurations.SetActiveAsync("glooko", true, null, CancellationToken.None);

        unattributed.ModifiedBy.Should().BeNull();
        JsonNode.Parse(unattributed.ConfigurationJson)!["enabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task TheToggleCreatesTheRowWhenTheConnectorHasNone()
    {
        var a = await SeedTenantAsync();

        var created = await Configurations(a).SetActiveAsync("libre", true, "rhys", CancellationToken.None);

        created.Should().Match<ConnectorConfigurationRecord>(c =>
            c.TenantId == a && c.ConnectorName == "libre" && c.ModifiedBy == "rhys");
        JsonNode.Parse(created.ConfigurationJson)!.ToJsonString().Should().Be("""{"enabled":true}""");
        (await Configurations(a).FindAsync("libre", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task AToggleBlockedByAConfigurationSaveKeepsTheSavedConfigurationAndSetsTheFlag()
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("glooko", """{"enabled":true,"region":"eu"}""", "owner", CancellationToken.None);

        await using var saver = new NpgsqlConnection(database.MigratorConnectionString);
        await saver.OpenAsync();
        await using var save = await saver.BeginTransactionAsync();
        await using (var cmd = saver.CreateCommand())
        {
            cmd.Transaction = save;
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, true);
                UPDATE connector_configurations SET configuration = '{"enabled":true,"region":"us"}'::jsonb
                WHERE tenant_id = @tenant::uuid AND connector_name = 'glooko'
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@tenant", a.ToString()));
            await cmd.ExecuteNonQueryAsync();
        }

        var toggle = Task.Run(() => Configurations(a).SetActiveAsync("glooko", false, "rhys", CancellationToken.None));
        await WaitForAWriterBlockedByAsync(saver);
        await save.CommitAsync();
        var toggled = await toggle;

        var config = JsonNode.Parse(toggled.ConfigurationJson)!.AsObject();
        config["region"]!.GetValue<string>().Should().Be("us");
        config["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"region":"us"}""")]
    [InlineData("""{"region":"us","enabled":true}""")]
    [InlineData("""{"region":"us","Enabled":true}""")]
    public async Task AConfigurationSaveAfterADisableKeepsTheConnectorDisabled(string saved)
    {
        var a = await SeedTenantAsync();
        var configurations = Configurations(a);
        await configurations.SaveConfigurationAsync("glooko", """{"enabled":true,"region":"eu"}""", "owner", CancellationToken.None);
        await configurations.SetActiveAsync("glooko", false, "rhys", CancellationToken.None);

        var record = await configurations.SaveConfigurationAsync("glooko", saved, "erik", CancellationToken.None);

        var config = JsonNode.Parse(record.ConfigurationJson)!.AsObject();
        config.ToDictionary(p => p.Key, p => p.Value!.ToJsonString()).Should().BeEquivalentTo(
            new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
        record.ModifiedBy.Should().Be("erik");
        (await configurations.FindAsync("glooko", CancellationToken.None))!.ConfigurationJson.Should().Be(record.ConfigurationJson);
    }

    [Fact]
    public async Task AConfigurationSaveKeepsTheStoredFlagWhateverCaseItIsStoredIn()
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("glooko", """{"Enabled":false,"region":"eu"}""", "owner", CancellationToken.None);

        var record = await Configurations(a).SaveConfigurationAsync("glooko", """{"region":"us"}""", "erik", CancellationToken.None);

        JsonNode.Parse(record.ConfigurationJson)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToJsonString())
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("Enabled")]
    public async Task AConfigurationSaveOntoAStoredRowThatHasNoFlagTakesTheSavedFlag(string spelling)
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveSecretsAsync("glooko", """{"password":"x"}""", "owner", CancellationToken.None);

        var record = await Configurations(a).SaveConfigurationAsync(
            "glooko", $$"""{"{{spelling}}":false,"region":"us"}""", "erik", CancellationToken.None);

        JsonNode.Parse(record.ConfigurationJson)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToJsonString())
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
        JsonNode.Parse(record.SecretsJson)!["password"]!.GetValue<string>().Should().Be("x");
    }

    [Fact]
    public async Task AConfigurationSaveOntoAStoredRowThatHasNoFlagLeavesItWithNoneWhenTheSavedHasNone()
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveSecretsAsync("glooko", """{"password":"x"}""", "owner", CancellationToken.None);

        var record = await Configurations(a).SaveConfigurationAsync("glooko", """{"region":"us"}""", "erik", CancellationToken.None);

        JsonNode.Parse(record.ConfigurationJson)!.AsObject().Select(p => p.Key).Should().Equal("region");
    }

    [Fact]
    public async Task TheFirstConfigurationSaveStoresTheFlagItCarriesSpelledEnabled()
    {
        var a = await SeedTenantAsync();

        var record = await Configurations(a).SaveConfigurationAsync("glooko", """{"Enabled":false,"region":"us"}""", "erik", CancellationToken.None);

        JsonNode.Parse(record.ConfigurationJson)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToJsonString())
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["enabled"] = "false", ["region"] = "\"us\"" });
    }

    [Fact]
    public async Task AConfigurationSaveBlockedByAToggleKeepsTheToggledFlagAndSavesTheConfiguration()
    {
        var a = await SeedTenantAsync();
        await Configurations(a).SaveConfigurationAsync("glooko", """{"enabled":true,"region":"eu"}""", "owner", CancellationToken.None);

        await using var toggler = new NpgsqlConnection(database.MigratorConnectionString);
        await toggler.OpenAsync();
        await using var toggle = await toggler.BeginTransactionAsync();
        await using (var cmd = toggler.CreateCommand())
        {
            cmd.Transaction = toggle;
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, true);
                UPDATE connector_configurations SET configuration = '{"enabled":false,"region":"eu"}'::jsonb
                WHERE tenant_id = @tenant::uuid AND connector_name = 'glooko'
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@tenant", a.ToString()));
            await cmd.ExecuteNonQueryAsync();
        }

        var save = Task.Run(() => Configurations(a).SaveConfigurationAsync(
            "glooko", """{"enabled":true,"region":"us"}""", "erik", CancellationToken.None));
        await WaitForAWriterBlockedByAsync(toggler);
        await toggle.CommitAsync();
        var saved = await save;

        var config = JsonNode.Parse(saved.ConfigurationJson)!.AsObject();
        config["region"]!.GetValue<string>().Should().Be("us");
        config["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    private async Task WaitForAWriterBlockedByAsync(NpgsqlConnection blocker)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid))";
        cmd.Parameters.Add(new NpgsqlParameter("@blocker", blocker.ProcessID));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((long)(await cmd.ExecuteScalarAsync())! == 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The writer never waited on the blocking transaction's row lock.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task AResetJobIsStoredAndReadOnAPlatformContext()
    {
        var tenant = await SeedTenantAsync();
        var jobs = new ConnectorResetJobRepository(new Factory(Context, tenant));
        var job = new ConnectorResetJobRecord(
            Guid.NewGuid(), tenant, "home", "Running", DateTime.UtcNow.Date, null, null, null, 1, 0, "[]");

        await jobs.SaveAsync(job, CancellationToken.None);
        await jobs.SaveAsync(job with { State = "Completed", CompletedConnectors = 1 }, CancellationToken.None);

        (await jobs.FindAsync(job.Id, CancellationToken.None)).Should().Be(job with { State = "Completed", CompletedConnectors = 1 });
    }
}
