using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RelabelInstanceWideRoleAuditRows</c> on a real PostgreSQL as the migrator over role rows with and
/// without a kind, a role row whose details were cleared, and rows of other event types with no kind.
/// </summary>
public class RelabelInstanceWideRoleAuditRowsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261123082547_KeySystemEventsOnOriginalId";

    internal static readonly Guid Tenant = Guid.Parse("a1111111-1111-7111-8111-111111113795");
    internal static readonly Guid KindlessAssigned = Guid.Parse("d1111111-1111-7111-8111-111111113795");
    internal static readonly Guid KindlessRemoved = Guid.Parse("d2222222-2222-7222-8222-222222223795");
    internal static readonly Guid TenantKind = Guid.Parse("d3333333-3333-7333-8333-333333333795");
    internal static readonly Guid Cleared = Guid.Parse("d4444444-4444-7444-8444-444444443795");
    internal static readonly Guid KindlessPermissionsChange = Guid.Parse("d5555555-5555-7555-8555-555555553795");
    internal static readonly Guid KindlessLogin = Guid.Parse("d6666666-6666-7666-8666-666666663795");
    internal static readonly Guid GlobalKind = Guid.Parse("d7777777-7777-7777-8777-777777773795");
    internal static readonly Guid GlobalPermissionsChange = Guid.Parse("d8888888-8888-7888-8888-888888883795");
    internal static readonly Guid TenantPermissionsChange = Guid.Parse("d9999999-9999-7999-8999-999999993795");

    private string _migrator = string.Empty;

    internal record AuditRow(string? Tenant, string? Details);

    internal Dictionary<Guid, AuditRow> Before { get; private set; } = [];
    internal Dictionary<Guid, AuditRow> After { get; private set; } = [];
    internal int RowsChangedBySecondRun { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "relabel_role_audit");
        _migrator = database.MigratorConnectionString;

        await NpgsqlTestCommands.ExecuteAsync(_migrator, $$"""
            INSERT INTO auth_audit_log (id, event_type, tenant_id, details, success, created_at)
            VALUES ('{{KindlessAssigned}}', 'role_assigned', '{{Tenant}}', '{"role":"admin"}', true, now()),
                   ('{{KindlessRemoved}}', 'role_removed', '{{Tenant}}', '{"role":"admin"}', true, now()),
                   ('{{TenantKind}}', 'role_assigned', '{{Tenant}}', '{"role":"viewer","kind":"tenant"}', true, now()),
                   ('{{Cleared}}', 'role_assigned', '{{Tenant}}', NULL, true, now()),
                   ('{{KindlessPermissionsChange}}', 'role_permissions_changed', '{{Tenant}}', '{"role":"Admin"}', true, now()),
                   ('{{KindlessLogin}}', 'login', '{{Tenant}}', '{"role":"admin"}', true, now()),
                   ('{{GlobalKind}}', 'role_assigned', NULL, '{"role":"admin","kind":"global"}', true, now()),
                   ('{{GlobalPermissionsChange}}', 'role_permissions_changed', '{{Tenant}}', '{"role":"Admin","kind":"global"}', true, now()),
                   ('{{TenantPermissionsChange}}', 'role_permissions_changed', '{{Tenant}}', '{"role":"Admin","kind":"tenant"}', true, now());
            """);
        Before = await ReadAsync();

        await using (var context = new NocturneDbContext(TestContexts.MigrationsOptions(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();

        After = await ReadAsync();
        RowsChangedBySecondRun = await NpgsqlTestCommands.ExecuteAsync(_migrator, RelabelInstanceWideRoleAuditRows.RelabelSql);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Dictionary<Guid, AuditRow>> ReadAsync()
    {
        var rows = new Dictionary<Guid, AuditRow>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id, tenant_id::text, details::text FROM auth_audit_log", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows[reader.GetGuid(0)] = new AuditRow(
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        return rows;
    }
}

[Trait("Category", "Integration")]
public class RelabelInstanceWideRoleAuditRowsTests(RelabelInstanceWideRoleAuditRowsFixture fixture)
    : IClassFixture<RelabelInstanceWideRoleAuditRowsFixture>
{
    public static TheoryData<Guid> Relabelled => new(
        RelabelInstanceWideRoleAuditRowsFixture.KindlessAssigned, RelabelInstanceWideRoleAuditRowsFixture.KindlessRemoved);

    public static TheoryData<Guid> Untouched => new(
        RelabelInstanceWideRoleAuditRowsFixture.TenantKind, RelabelInstanceWideRoleAuditRowsFixture.Cleared,
        RelabelInstanceWideRoleAuditRowsFixture.KindlessPermissionsChange, RelabelInstanceWideRoleAuditRowsFixture.KindlessLogin,
        RelabelInstanceWideRoleAuditRowsFixture.GlobalKind, RelabelInstanceWideRoleAuditRowsFixture.TenantPermissionsChange);

    [Theory]
    [MemberData(nameof(Relabelled))]
    public void A_role_row_with_no_kind_says_global_and_loses_its_tenant(Guid row)
    {
        fixture.Before[row].Tenant.Should().NotBeNull();
        fixture.After[row].Tenant.Should().BeNull();
        fixture.After[row].Details.Should().Be("""{"kind": "global", "role": "admin"}""");
    }

    [Fact]
    public void A_global_permission_change_loses_its_tenant_and_keeps_its_details()
    {
        var row = RelabelInstanceWideRoleAuditRowsFixture.GlobalPermissionsChange;

        fixture.Before[row].Tenant.Should().NotBeNull();
        fixture.After[row].Tenant.Should().BeNull();
        fixture.After[row].Details.Should().Be(fixture.Before[row].Details);
    }

    [Theory]
    [MemberData(nameof(Untouched))]
    public void Every_other_row_is_unchanged(Guid row) => fixture.After[row].Should().Be(fixture.Before[row]);

    [Fact]
    public void A_second_run_changes_nothing() => fixture.RowsChangedBySecondRun.Should().Be(0);
}
