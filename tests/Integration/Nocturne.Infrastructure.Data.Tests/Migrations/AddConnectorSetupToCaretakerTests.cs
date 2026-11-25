using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddConnectorSetupToCaretaker</c> on a real PostgreSQL as the migrator over a built-in
/// Caretaker of each set it was ever seeded with, Caretakers an owner edited, a custom role with a
/// seeded Caretaker's permissions, and the other built-in roles. A second database runs it up and back
/// down.
/// </summary>
public class AddConnectorSetupToCaretakerFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261122191618_AddDeviceStatusExtrasSource";

    internal static readonly string[] FirstSeed =
    [
        "glucose.read", "treatments.readwrite", "devices.read", "therapy.read", "alerts.read", "reports.read",
        "heartrate.read", "stepcount.read", "food.read", "sleep.read",
    ];

    internal static readonly string[] RenamedSeed =
    [
        "glucose.read", "treatments.readwrite", "devices.read", "food.read", "heartrate.read", "stepcount.read",
        "reports.read", "therapy.read", "alerts.readwrite", "sleep.read",
    ];

    internal static readonly string[] DeviceSeed = [.. RenamedSeed, "device.notify", "device.actuate"];

    internal static readonly Guid FirstCaretaker = Guid.CreateVersion7();
    internal static readonly Guid RenamedCaretaker = Guid.CreateVersion7();
    internal static readonly Guid DeviceCaretaker = Guid.CreateVersion7();
    internal static readonly Guid ReorderedCaretaker = Guid.CreateVersion7();
    internal static readonly Guid CaretakerWithExtra = Guid.CreateVersion7();
    internal static readonly Guid CaretakerWithOneRemoved = Guid.CreateVersion7();
    internal static readonly Guid CustomCaretaker = Guid.CreateVersion7();
    internal static readonly Guid CaretakerWithConnectorSetup = Guid.CreateVersion7();
    internal static readonly Guid Viewer = Guid.CreateVersion7();
    internal static readonly Guid Clinician = Guid.CreateVersion7();
    internal static readonly Guid Admin = Guid.CreateVersion7();
    internal static readonly Guid Owner = Guid.CreateVersion7();

    internal static readonly Guid[] Unedited = [FirstCaretaker, RenamedCaretaker, DeviceCaretaker, ReorderedCaretaker];

    private string _migrator = string.Empty;

    internal Dictionary<Guid, string[]> Before { get; private set; } = [];
    internal Dictionary<Guid, string[]> After { get; private set; } = [];
    internal Dictionary<Guid, string[]> AfterSecondRun { get; private set; } = [];
    internal int RowsChangedBySecondRun { get; private set; }
    internal Dictionary<Guid, string[]> AfterDown { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "caretaker_connectors");
        _migrator = database.MigratorConnectionString;
        await SeedAsync(_migrator);
        Before = await ReadAsync(_migrator);
        await MigrateAsync(_migrator, null);
        After = await ReadAsync(_migrator);
        RowsChangedBySecondRun = await NpgsqlTestCommands.ExecuteAsync(_migrator, AddConnectorSetupToCaretaker.UpSql);
        AfterSecondRun = await ReadAsync(_migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "caretaker_connectors_down");
        await SeedAsync(down.MigratorConnectionString);
        await MigrateAsync(down.MigratorConnectionString, null);
        await MigrateAsync(down.MigratorConnectionString, PriorMigration);
        AfterDown = await ReadAsync(down.MigratorConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task SeedAsync(string connectionString)
    {
        string[] reordered = [.. Enumerable.Reverse(DeviceSeed), "sleep.read"];
        (Guid Id, string Slug, bool IsSystem, string[] Permissions)[] roles =
        [
            (FirstCaretaker, "caretaker", true, FirstSeed),
            (RenamedCaretaker, "caretaker", true, RenamedSeed),
            (DeviceCaretaker, "caretaker", true, DeviceSeed),
            (ReorderedCaretaker, "caretaker", true, reordered),
            (CaretakerWithExtra, "caretaker", true, [.. DeviceSeed, "glucose.readwrite"]),
            (CaretakerWithOneRemoved, "caretaker", true, [.. DeviceSeed.Where(p => p != "alerts.readwrite")]),
            (CustomCaretaker, "caretaker", false, DeviceSeed),
            (CaretakerWithConnectorSetup, "caretaker", true, [.. DeviceSeed, "connectors.readwrite"]),
            (Viewer, "viewer", true, ["glucose.read", "reports.read", "device.notify", "device.actuate"]),
            (Clinician, "clinician", true, [.. DeviceSeed.Select(p => p.Replace(".readwrite", ".read"))]),
            (Admin, "admin", true, ["glucose.readwrite", "treatments.readwrite", "tenant.settings", "members.manage"]),
            (Owner, "owner", true, ["*"]),
        ];

        foreach (var (id, slug, isSystem, permissions) in roles)
        {
            var tenant = Guid.CreateVersion7();
            await NpgsqlTestCommands.ExecuteAsync(connectionString, $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'caretaker-{tenant:N}', 'Site', true, now(), now());
                INSERT INTO tenant_roles (id, tenant_id, name, slug, permissions, is_system, sys_created_at, sys_updated_at)
                VALUES ('{id}', '{tenant}', '{slug}', '{slug}', '{JsonSerializer.Serialize(permissions)}', {(isSystem ? "true" : "false")}, now(), now());
                """);
        }
    }

    private static async Task<Dictionary<Guid, string[]>> ReadAsync(string connectionString)
    {
        var rows = new Dictionary<Guid, string[]>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, permissions::text FROM tenant_roles", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows[reader.GetGuid(0)] = JsonSerializer.Deserialize<string[]>(reader.GetString(1))!;
        return rows;
    }

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddConnectorSetupToCaretakerTests(AddConnectorSetupToCaretakerFixture fixture)
    : IClassFixture<AddConnectorSetupToCaretakerFixture>
{
    private const string ConnectorSetup = "connectors.readwrite";

    public static TheoryData<Guid> Unedited => new(AddConnectorSetupToCaretakerFixture.Unedited);

    private static readonly Guid[] Edited =
    [
        AddConnectorSetupToCaretakerFixture.CaretakerWithExtra,
        AddConnectorSetupToCaretakerFixture.CaretakerWithOneRemoved,
        AddConnectorSetupToCaretakerFixture.CustomCaretaker,
        AddConnectorSetupToCaretakerFixture.Viewer,
        AddConnectorSetupToCaretakerFixture.Clinician,
        AddConnectorSetupToCaretakerFixture.Admin,
        AddConnectorSetupToCaretakerFixture.Owner,
    ];

    public static TheoryData<Guid> Untouched => new([.. Edited, AddConnectorSetupToCaretakerFixture.CaretakerWithConnectorSetup]);

    public static TheoryData<Guid> UntouchedByDown => new(Edited);

    [Theory]
    [MemberData(nameof(Unedited))]
    public void An_unedited_caretaker_of_any_seed_gains_connector_setup_and_keeps_the_rest(Guid role)
    {
        fixture.Before[role].Should().NotContain(ConnectorSetup);
        fixture.After[role].Should().Equal([.. fixture.Before[role], ConnectorSetup]);
    }

    [Theory]
    [MemberData(nameof(Untouched))]
    public void A_role_that_is_not_an_unedited_caretaker_is_unchanged(Guid role) =>
        fixture.After[role].Should().Equal(fixture.Before[role]);

    [Fact]
    public void A_second_run_changes_nothing()
    {
        fixture.RowsChangedBySecondRun.Should().Be(0);
        fixture.AfterSecondRun.Should().BeEquivalentTo(fixture.After);
    }

    [Theory]
    [MemberData(nameof(Unedited))]
    public void Down_takes_connector_setup_back_from_an_unedited_caretaker(Guid role) =>
        fixture.AfterDown[role].Should().Equal(fixture.Before[role]);

    [Theory]
    [MemberData(nameof(UntouchedByDown))]
    public void Down_leaves_every_role_that_is_not_an_unedited_caretaker(Guid role) =>
        fixture.AfterDown[role].Should().Equal(fixture.Before[role]);

    [Fact]
    public void Down_also_takes_connector_setup_from_a_caretaker_seeded_with_it() =>
        fixture.AfterDown[AddConnectorSetupToCaretakerFixture.CaretakerWithConnectorSetup]
            .Should().Equal(AddConnectorSetupToCaretakerFixture.DeviceSeed,
                "Down cannot tell a caretaker seeded with connector setup from one Up gave it");
}
