using FluentAssertions;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Regression coverage for the <c>ix_devices_category_type_serial</c> unique index.
///
/// The index must be scoped by <c>tenant_id</c>. Devices are tenant-owned, and the
/// (category, type, serial) triple is routinely shared across patients — a pump's
/// manufacturer/model ("Insulet"/"Omnipod 5") or the generic Loop uploader identity
/// ("iPhone"/"unknown"). When the index was global, the first tenant to register such a
/// device permanently blocked every other tenant's insert, surfacing as a 500 on
/// devicestatus ingestion (and a "network error" in Loop while adding the service).
///
/// Raw NpgsqlConnection (not EF) so the assertion is about what PostgreSQL actually
/// enforces after the full migration chain runs, independent of the ORM.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class DeviceUniqueIndexTests
{
    private readonly RlsCompletenessFixture _fx;

    public DeviceUniqueIndexTests(RlsCompletenessFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task TwoTenants_CanEachOwn_SameCategoryTypeSerialDevice()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await InsertTenantAsync(conn, tenantA);
        await InsertTenantAsync(conn, tenantB);

        // Two different patients running Loop on an iPhone produce the identical
        // generic uploader identity. Both inserts must succeed.
        await SetCurrentTenantAsync(conn, tenantA);
        await InsertDeviceAsync(conn, tenantA, "Uploader", "iPhone", "unknown");

        await SetCurrentTenantAsync(conn, tenantB);
        var act = () => InsertDeviceAsync(conn, tenantB, "Uploader", "iPhone", "unknown");

        await act.Should().NotThrowAsync(
            "the device unique index is scoped per tenant; two tenants can legitimately own the " +
            "same pump model or uploader identity without colliding");
    }

    [Fact]
    public async Task SameTenant_CannotInsert_DuplicateLiveDevice()
    {
        var tenant = Guid.NewGuid();

        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await InsertTenantAsync(conn, tenant);
        await SetCurrentTenantAsync(conn, tenant);

        await InsertDeviceAsync(conn, tenant, "InsulinPump", "Insulet", "Omnipod 5");
        var act = () => InsertDeviceAsync(conn, tenant, "InsulinPump", "Insulet", "Omnipod 5");

        var thrown = await act.Should().ThrowAsync<PostgresException>();
        thrown.Which.SqlState.Should().Be(
            "23505",
            "within a single tenant the live (category, type, serial) device identity must remain unique");
    }

    private static async Task InsertTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants
                (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES
                (@id, @slug, 'device-index-test', true, now(), now())
            """;
        AddParam(cmd, "@id", tenantId);
        AddParam(cmd, "@slug", $"dev-{tenantId:N}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertDeviceAsync(
        NpgsqlConnection conn, Guid tenantId, string category, string type, string serial)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices
                (id, tenant_id, category, type, serial, first_seen_timestamp, last_seen_timestamp)
            VALUES
                (gen_random_uuid(), @tid, @category, @type, @serial, now(), now())
            """;
        AddParam(cmd, "@tid", tenantId);
        AddParam(cmd, "@category", category);
        AddParam(cmd, "@type", type);
        AddParam(cmd, "@serial", serial);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SetCurrentTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
        AddParam(cmd, "@tid", tenantId.ToString());
        await cmd.ExecuteScalarAsync();
    }

    private static void AddParam(NpgsqlCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
