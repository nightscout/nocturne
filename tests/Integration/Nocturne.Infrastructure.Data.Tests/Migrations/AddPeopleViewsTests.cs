using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddPeopleViews</c> on a real PostgreSQL as the migrator, then exercises the table's checks, unique name and
/// cascade, its strict subject pin, and <see cref="PeopleViewRepository"/> holding a viewer to the limit and to one
/// name under the runtime role, creates at once included. A second database runs it up and back down.
/// </summary>
public class AddPeopleViewsFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261127151739_FilterHealthWatermarkIndexes";

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_people_views");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_people_views_down");
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
            VALUES ('{subject}', 'views-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal PeopleViewRepository Repository() => new(new SuppliedContextFactory(() => TestContexts.Context(App)));

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddPeopleViewsTests(AddPeopleViewsFixture fixture) : IClassFixture<AddPeopleViewsFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static PeopleViewDraft Draft(string name) => new(name, PeopleQuery.Empty, PeopleLens.Now, null);

    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    private static string Insert(Guid subject, string? name = null, string lens = "now", string days = "NULL", int position = 0) => $"""
        INSERT INTO people_views (id, subject_id, name, position, query, lens, review_days)
        VALUES ('{Guid.CreateVersion7()}', '{subject}', '{name ?? Guid.NewGuid().ToString("N")}', {position}, '{"{}"}', '{lens}', {days})
        """;

    [Fact]
    public async Task The_table_is_under_a_forced_subject_pin()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, """
            SELECT count(*) FROM pg_class c
             WHERE c.relname = 'people_views' AND c.relrowsecurity AND c.relforcerowsecurity
               AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'subject_pin' AND p.polpermissive)
            """)).Should().Be(1);
    }

    [Fact]
    public async Task A_subject_pinned_context_reads_and_writes_only_its_own_views()
    {
        var viewer = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer));
        await ExecuteAsync(Pin.Subject(other), Insert(other));

        var count = $"SELECT count(*) FROM people_views WHERE subject_id IN ('{viewer}', '{other}')";
        (await ScalarAsync(Pin.Subject(viewer), count)).Should().Be(1);
        (await ScalarAsync(Pin.Subject(other), count)).Should().Be(1);
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Subject(viewer), Insert(other)), PostgresErrorCodes.InsufficientPrivilege);
        (await RowsAsync(Pin.Subject(viewer), $"UPDATE people_views SET position = 9 WHERE subject_id = '{other}'")).Should().Be(0);
        (await RowsAsync(Pin.Subject(viewer), $"DELETE FROM people_views WHERE subject_id = '{other}'")).Should().Be(0);
        await ShouldViolateAsync(
            () => ExecuteAsync(Pin.Subject(viewer), $"UPDATE people_views SET subject_id = '{other}' WHERE subject_id = '{viewer}'"),
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_tenant_context_or_a_context_with_no_subject_reads_and_writes_no_view()
    {
        var viewer = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer));
        var count = $"SELECT count(*) FROM people_views WHERE subject_id = '{viewer}'";

        (await ScalarAsync(Pin.Tenant(Guid.CreateVersion7()), count)).Should().Be(0);
        (await ScalarAsync(Pin.Subject(null), count)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, count)).Should().Be(0, "the migrator obeys the forced policy");
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Tenant(Guid.CreateVersion7()), Insert(viewer)), PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_view_holds_a_period_only_for_the_review_lens_and_then_one_of_the_review_periods()
    {
        var viewer = await fixture.NewSubjectAsync();
        var pin = Pin.Subject(viewer);

        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, lens: "now", days: "14")), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, lens: "review")), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, lens: "review", days: "7")), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, lens: "later")), PostgresErrorCodes.CheckViolation);
        await ShouldViolateAsync(() => ExecuteAsync(pin, Insert(viewer, position: -1)), PostgresErrorCodes.CheckViolation);
        foreach (var days in new[] { "14", "30", "90" })
            await ExecuteAsync(pin, Insert(viewer, lens: "review", days: days));
    }

    [Fact]
    public async Task A_name_is_one_view_of_a_subject_ignoring_case_and_free_for_another_subject()
    {
        var viewer = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer, "Night shift"));

        await ShouldViolateAsync(() => ExecuteAsync(Pin.Subject(viewer), Insert(viewer, "NIGHT SHIFT")), PostgresErrorCodes.UniqueViolation);
        await ExecuteAsync(Pin.Subject(other), Insert(other, "Night shift"));
    }

    [Fact]
    public async Task A_subjects_views_go_with_the_subject()
    {
        var viewer = await fixture.NewSubjectAsync();
        await ExecuteAsync(Pin.Subject(viewer), Insert(viewer));

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM subjects WHERE id = '{viewer}'");

        (await ScalarAsync(Pin.Subject(viewer), "SELECT count(*) FROM people_views")).Should().Be(0);
    }

    [Fact]
    public async Task The_repository_holds_a_viewer_to_the_limit_and_refuses_the_next()
    {
        var viewer = await fixture.NewSubjectAsync();
        var views = fixture.Repository();

        for (var i = 0; i < PeopleViewLimits.PerViewer; i++)
            (await views.CreateAsync(viewer, Draft($"View {i}"), Ct)).Change.Should().Be(PeopleViewChange.Changed);
        var refused = await views.CreateAsync(viewer, Draft("One more"), Ct);

        refused.Should().Be(new PeopleViewWrite(PeopleViewChange.LimitReached, null));
        var held = await views.GetOfSubjectAsync(viewer, Ct);
        held.Select(v => v.Name).Should().Equal(Enumerable.Range(0, PeopleViewLimits.PerViewer).Select(i => $"View {i}"));
        held.Select(v => v.Position).Should().Equal(Enumerable.Range(0, PeopleViewLimits.PerViewer), "each view is added after the last");
    }

    [Fact]
    public async Task Creates_at_once_never_pass_the_limit_together()
    {
        var viewer = await fixture.NewSubjectAsync();
        var views = fixture.Repository();
        for (var i = 0; i < PeopleViewLimits.PerViewer - 5; i++)
            await views.CreateAsync(viewer, Draft($"View {i}"), Ct);
        using var start = new SemaphoreSlim(0);

        var creates = Enumerable.Range(0, 20).Select(async i =>
        {
            await start.WaitAsync();
            return await fixture.Repository().CreateAsync(viewer, Draft($"At once {i}"), Ct);
        }).ToList();
        start.Release(creates.Count);
        var made = await Task.WhenAll(creates);

        made.Count(w => w.Change == PeopleViewChange.Changed).Should().Be(5);
        made.Where(w => w.Change != PeopleViewChange.Changed).Should().OnlyContain(w => w.Change == PeopleViewChange.LimitReached);
        (await views.GetOfSubjectAsync(viewer, Ct)).Select(v => v.Position).Should().OnlyHaveUniqueItems().And.HaveCount(PeopleViewLimits.PerViewer);
    }

    [Fact]
    public async Task Creates_of_one_name_at_once_make_one_view()
    {
        var viewer = await fixture.NewSubjectAsync();
        using var start = new SemaphoreSlim(0);

        var creates = Enumerable.Range(0, 8).Select(async i =>
        {
            await start.WaitAsync();
            return await fixture.Repository().CreateAsync(viewer, Draft(i % 2 == 0 ? "Ward" : "WARD"), Ct);
        }).ToList();
        start.Release(creates.Count);
        var made = await Task.WhenAll(creates);

        made.Count(w => w.Change == PeopleViewChange.Changed).Should().Be(1);
        made.Where(w => w.Change != PeopleViewChange.Changed).Should().OnlyContain(w => w.Change == PeopleViewChange.NameTaken);
    }

    [Fact]
    public async Task A_name_the_unique_index_refuses_after_the_check_passed_is_name_taken()
    {
        var viewer = await fixture.NewSubjectAsync();
        var views = fixture.Repository();
        var day = (await views.CreateAsync(viewer, Draft("Day"), Ct)).View!;

        (await ClashAsync(viewer, "Clash", () => views.CreateAsync(viewer, Draft("clash"), Ct)))
            .Change.Should().Be(PeopleViewChange.NameTaken);
        (await ClashAsync(viewer, "Clash too", () => views.UpdateAsync(viewer, day.Id, Draft("CLASH TOO"), Ct)))
            .Change.Should().Be(PeopleViewChange.NameTaken);
        (await views.GetOfSubjectAsync(viewer, Ct)).Select(v => v.Name).Should().BeEquivalentTo("Day", "Clash", "Clash too");
    }

    /// <summary>
    /// Runs <paramref name="write"/> while another transaction, outside the repository's lock, holds an uncommitted view
    /// named <paramref name="name"/>: the write's check cannot see it, its insert waits on the unique index, and the
    /// other transaction then commits.
    /// </summary>
    private async Task<PeopleViewWrite> ClashAsync(Guid viewer, string name, Func<Task<PeopleViewWrite>> write)
    {
        await using var conn = await PinnedAsync(Pin.Subject(viewer));
        await using var tx = await conn.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(Insert(viewer, name, position: 50), conn, tx))
            await insert.ExecuteNonQueryAsync();

        var written = write();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!written.IsCompleted
               && await NpgsqlTestCommands.ScalarAsync<long>(fixture.App, """
                   SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'
                   """) == 0)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the write should wait on the uncommitted name");
            await Task.Delay(50);
        }

        await tx.CommitAsync();
        return await written;
    }

    [Fact]
    public async Task A_name_another_view_of_the_subject_holds_is_refused_ignoring_case_and_a_view_keeps_its_own()
    {
        var viewer = await fixture.NewSubjectAsync();
        var views = fixture.Repository();
        var night = (await views.CreateAsync(viewer, Draft("Night shift"), Ct)).View!;
        var day = (await views.CreateAsync(viewer, Draft("Day shift"), Ct)).View!;

        (await views.CreateAsync(viewer, Draft("night SHIFT"), Ct)).Change.Should().Be(PeopleViewChange.NameTaken);
        (await views.UpdateAsync(viewer, day.Id, Draft("NIGHT shift"), Ct)).Change.Should().Be(PeopleViewChange.NameTaken);
        var renamed = await views.UpdateAsync(viewer, night.Id, Draft("NIGHT SHIFT"), Ct);

        renamed.Change.Should().Be(PeopleViewChange.Changed);
        renamed.View!.Name.Should().Be("NIGHT SHIFT");
    }

    [Fact]
    public async Task A_view_reads_back_as_it_was_written_and_changes_only_for_its_subject()
    {
        var viewer = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        var views = fixture.Repository();
        var query = new PeopleQuery(
            "ann",
            [
                new PeopleFilter(PeopleFilterKind.Fact, "glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, null, null, null, null),
                new PeopleFilter(PeopleFilterKind.Therapy, null, null, null, null, null, null, [TherapyType.Pump, TherapyType.Injections], null, null),
                new PeopleFilter(PeopleFilterKind.InGroup, null, null, null, null, null, null, null, null, Guid.CreateVersion7()),
            ],
            new PeopleSort(PeopleSortColumn.TimeInRange, PeopleSortDirection.Descending));
        var own = (await views.CreateAsync(viewer, new PeopleViewDraft("Review", query, PeopleLens.Review, 90), Ct)).View!;
        var theirs = (await views.CreateAsync(other, Draft("Theirs"), Ct)).View!;

        var read = (await views.GetOfSubjectAsync(viewer, Ct)).Single();
        (await views.UpdateAsync(viewer, theirs.Id, Draft("Mine now"), Ct)).Change.Should().Be(PeopleViewChange.NotFound);
        (await views.DeleteAsync(viewer, theirs.Id, Ct)).Should().BeFalse();
        var changed = (await views.UpdateAsync(viewer, own.Id, Draft("Now"), Ct)).View!;

        read.Should().BeEquivalentTo(own, o => o.Using<DateTime>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTime>());
        read.Query.Should().BeEquivalentTo(query);
        (read.Lens, read.ReviewDays).Should().Be((PeopleLens.Review, (int?)90));
        (changed.Name, changed.Lens, changed.ReviewDays, changed.Position).Should().Be(("Now", PeopleLens.Now, (int?)null, own.Position));
        changed.Query.Should().BeEquivalentTo(PeopleQuery.Empty);
        changed.UpdatedAt.Should().BeOnOrAfter(own.UpdatedAt);
        (await views.GetOfSubjectAsync(other, Ct)).Should().ContainSingle().Which.Name.Should().Be("Theirs");
        (await views.DeleteAsync(viewer, own.Id, Ct)).Should().BeTrue();
        (await views.GetOfSubjectAsync(viewer, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reorder_keeps_every_view_and_one_that_does_not_name_each_view_once_changes_nothing()
    {
        var viewer = await fixture.NewSubjectAsync();
        var other = await fixture.NewSubjectAsync();
        var views = fixture.Repository();
        var ids = new List<Guid>();
        foreach (var name in new[] { "A", "B", "C" })
            ids.Add((await views.CreateAsync(viewer, Draft(name), Ct)).View!.Id);
        var foreign = (await views.CreateAsync(other, Draft("X"), Ct)).View!.Id;

        (await views.ReorderAsync(viewer, [ids[2], ids[0]], Ct)).Should().BeNull();
        (await views.ReorderAsync(viewer, [ids[2], ids[0], ids[0]], Ct)).Should().BeNull();
        (await views.ReorderAsync(viewer, [ids[2], ids[0], ids[1], ids[1]], Ct)).Should().BeNull();
        (await views.ReorderAsync(viewer, [ids[2], ids[0], foreign], Ct)).Should().BeNull();
        (await views.GetOfSubjectAsync(viewer, Ct)).Select(v => v.Name).Should().Equal("A", "B", "C");

        var reordered = await views.ReorderAsync(viewer, [ids[2], ids[0], ids[1]], Ct);

        reordered!.Select(v => (v.Name, v.Position)).Should().Equal(("C", 0), ("A", 1), ("B", 2));
        (await views.GetOfSubjectAsync(viewer, Ct)).Select(v => v.Name).Should().Equal("C", "A", "B");
        (await views.GetOfSubjectAsync(other, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Down_drops_the_table()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'people_views'
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
