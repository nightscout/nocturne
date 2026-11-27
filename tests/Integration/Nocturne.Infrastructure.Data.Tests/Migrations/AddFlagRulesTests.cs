using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddFlagRules</c> on a real PostgreSQL as the migrator, then exercises the table's checks and cascades, its
/// strict subject pin, and <see cref="FlagRuleRepository"/> holding a viewer to the limit under the runtime role,
/// creates at once included. A second database runs it up and back down.
/// </summary>
public class AddFlagRulesFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261127020314_AddPageViewCounts";

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_flag_rules");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_flag_rules_down");
        DownMigrator = down.MigratorConnectionString;
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<Guid> NewSubjectAsync()
    {
        var subject = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{subject}', 'rules-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewTeamAsync()
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');");
        return team;
    }

    internal FlagRuleRepository Repository() => new(new SuppliedContextFactory(() => TestContexts.Context(App)));

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddFlagRulesTests(AddFlagRulesFixture fixture) : IClassFixture<AddFlagRulesFixture>
{
    private static readonly FlagRuleDraft Draft =
        new("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Warn);

    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    private static string Insert(Guid? subject, Guid? team = null, string comparison = "above", string level = "warn", string days = "14") => $"""
        INSERT INTO flag_rules (id, subject_id, team_id, dataset, field, days, comparison, value, level)
        VALUES ('{Guid.CreateVersion7()}', {Literal(subject)}, {Literal(team)}, 'glucose_statistics', 'timeBelow', {days}, '{comparison}', 4, '{level}')
        """;

    private static string Literal(Guid? id) => id is { } value ? $"'{value}'" : "NULL";

    [Fact]
    public async Task The_table_is_under_a_forced_subject_pin_alone()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname = 'flag_rules' AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'subject_pin' AND p.polpermissive)
            """)).Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid WHERE c.relname = 'flag_rules'
            """)).Should().Be(1);
    }

    [Fact]
    public async Task A_subject_pinned_context_reads_and_writes_only_its_own_rules()
    {
        var viewer = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer));
        await ExecuteAsync(Pin.Subject(other), Insert(other));

        var count = $"SELECT count(*) FROM flag_rules WHERE subject_id IN ('{viewer}', '{other}')";
        (await ScalarAsync(Pin.Subject(viewer), count)).Should().Be(1);
        (await ScalarAsync(Pin.Subject(other), count)).Should().Be(1);
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Subject(viewer), Insert(other)), PostgresErrorCodes.InsufficientPrivilege);
        (await RowsAsync(Pin.Subject(viewer), $"UPDATE flag_rules SET value = 9 WHERE subject_id = '{other}'")).Should().Be(0);
        (await RowsAsync(Pin.Subject(viewer), $"DELETE FROM flag_rules WHERE subject_id = '{other}'")).Should().Be(0);
        await ShouldViolateAsync(
            () => ExecuteAsync(Pin.Subject(viewer), $"UPDATE flag_rules SET subject_id = '{other}' WHERE subject_id = '{viewer}'"),
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_tenant_context_or_a_context_with_no_subject_reads_and_writes_no_rule()
    {
        var viewer = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer));
        var count = $"SELECT count(*) FROM flag_rules WHERE subject_id = '{viewer}'";

        (await ScalarAsync(Pin.Tenant(Guid.CreateVersion7()), count)).Should().Be(0);
        (await ScalarAsync(Pin.Subject(null), count)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, count)).Should().Be(0, "the migrator obeys the forced policy");
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Tenant(Guid.CreateVersion7()), Insert(viewer)), PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_rule_has_exactly_one_owner_and_only_the_stored_comparisons_levels_and_ranges()
    {
        var viewer = await fixture.NewSubjectAsync();
        var team = await fixture.NewTeamAsync();
        var pin = Pin.Subject(viewer);

        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, team)), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, comparison: "at_least")), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, level: "urgent")), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, days: "0")), PostgresErrorCodes.CheckViolation);
        await ExecuteAsync(pin, Insert(viewer, days: "NULL"));
    }

    [Fact]
    public async Task A_subjects_rules_go_with_the_subject()
    {
        var viewer = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer));

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{viewer}'");

        (await ScalarAsync(Pin.Subject(viewer), "SELECT count(*) FROM flag_rules")).Should().Be(0);
    }

    [Fact]
    public async Task The_repository_holds_a_viewer_to_the_limit_and_refuses_the_next()
    {
        var viewer = await fixture.NewSubjectAsync();
        var rules = fixture.Repository();

        for (var i = 0; i < FlagRuleLimits.PerViewer; i++)
            (await rules.CreateAsync(viewer, Draft with { Value = i }, CancellationToken.None)).Should().NotBeNull();
        var refused = await rules.CreateAsync(viewer, Draft, CancellationToken.None);

        refused.Should().BeNull();
        (await rules.GetOfSubjectAsync(viewer, CancellationToken.None)).Select(r => r.Value)
            .Should().Equal(Enumerable.Range(0, FlagRuleLimits.PerViewer).Select(i => (double)i), "rules are listed oldest first");
    }

    [Fact]
    public async Task Creates_at_once_never_pass_the_limit_together()
    {
        var viewer = await fixture.NewSubjectAsync();
        var rules = fixture.Repository();
        for (var i = 0; i < FlagRuleLimits.PerViewer - 5; i++)
            await rules.CreateAsync(viewer, Draft, CancellationToken.None);
        using var start = new SemaphoreSlim(0);

        var creates = Enumerable.Range(0, 20).Select(async _ =>
        {
            await start.WaitAsync();
            return await fixture.Repository().CreateAsync(viewer, Draft, CancellationToken.None);
        }).ToList();
        start.Release(creates.Count);
        var made = await Task.WhenAll(creates);

        made.Count(r => r is not null).Should().Be(5);
        (await rules.GetOfSubjectAsync(viewer, CancellationToken.None)).Should().HaveCount(FlagRuleLimits.PerViewer);
    }

    [Fact]
    public async Task The_repository_changes_and_deletes_only_the_subjects_own_rules()
    {
        var viewer = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        var rules = fixture.Repository();
        var own = (await rules.CreateAsync(viewer, Draft, CancellationToken.None))!;
        var theirs = (await rules.CreateAsync(other, Draft, CancellationToken.None))!;

        (await rules.UpdateAsync(viewer, theirs.Id, Draft with { Value = 9 }, CancellationToken.None)).Should().BeNull();
        (await rules.DeleteAsync(viewer, theirs.Id, CancellationToken.None)).Should().BeFalse();
        var changed = await rules.UpdateAsync(viewer, own.Id, Draft with { Value = 9, Level = NotificationUrgency.Hazard, Days = null }, CancellationToken.None);

        (changed!.Value, changed.Level, changed.Days).Should().Be((9.0, NotificationUrgency.Hazard, (int?)null));
        (await rules.GetOfSubjectAsync(other, CancellationToken.None)).Should().ContainSingle().Which.Value.Should().Be(4);
        (await rules.DeleteAsync(viewer, own.Id, CancellationToken.None)).Should().BeTrue();
        (await rules.GetOfSubjectAsync(viewer, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Down_drops_the_table()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'flag_rules'
            """)).Should().Be(0);
    }

    private sealed record Pin(string Setting, Guid? Value)
    {
        public static Pin Subject(Guid? subject) => new("app.current_subject_id", subject);

        public static Pin Tenant(Guid? tenant) => new("app.current_tenant_id", tenant);
    }

    private async Task<long> ScalarAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(Pin pin, string sql) => await RowsAsync(pin, sql);

    private async Task<int> RowsAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> PinnedAsync(Pin pin)
    {
        var conn = new NpgsqlConnection(fixture.App);
        await conn.OpenAsync();
        await using var set = new NpgsqlCommand($"SELECT set_config('{pin.Setting}', '{pin.Value?.ToString() ?? string.Empty}', false)", conn);
        await set.ExecuteNonQueryAsync();
        return conn;
    }
}
