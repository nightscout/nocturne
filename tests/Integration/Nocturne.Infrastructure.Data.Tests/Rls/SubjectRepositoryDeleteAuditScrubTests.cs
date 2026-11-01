using Nocturne.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What <see cref="SubjectRepository.DeleteAsync"/> leaves of the deleted subject in
/// <c>mutation_audit_log</c> and <c>read_access_log</c>, run as <c>nocturne_app</c> under the live
/// policies, in a tenant the subject still belongs to and in one it has left.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SubjectRepositoryDeleteAuditScrubTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private readonly Guid _memberTenant = Guid.NewGuid();
    private readonly Guid _formerTenant = Guid.NewGuid();
    private readonly Guid _leaver = Guid.CreateVersion7();
    private readonly Guid _bystander = Guid.CreateVersion7();

    public SubjectRepositoryDeleteAuditScrubTests(RlsCompletenessFixture fx) => _fx = fx;

    private Guid[] Tenants => [_memberTenant, _formerTenant];

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);

        await using var conn = await _fx.OpenMigratorConnectionAsync();
        foreach (var tenant in Tenants)
        {
            await ExecuteAsync(conn, """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'audit-scrub', true, now(), now())
                """, ("id", tenant), ("slug", $"scrub-{tenant:N}"));
        }

        foreach (var subject in new[] { _leaver, _bystander })
        {
            await ExecuteAsync(conn, """
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, true, false, now(), now(), 'Approved')
                """, ("id", subject), ("name", Name(subject)));
            await ExecuteAsync(conn, """
                INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenant, @subject, '[]'::jsonb, now(), now(), false)
                """, ("id", Guid.CreateVersion7()), ("tenant", _memberTenant), ("subject", subject));
        }

        foreach (var tenant in Tenants)
        {
            await SetTenantAsync(conn, tenant);
            foreach (var actor in new[] { _leaver, _bystander })
            {
                await ExecuteAsync(conn, """
                    INSERT INTO mutation_audit_log (id, tenant_id, entity_type, action, actor_subject_id, actor_name, ip_address, created_at)
                    VALUES (gen_random_uuid(), @tenant, 'SensorGlucose', 'update', @actor, @name, @ip, now())
                    """, ("tenant", tenant), ("actor", actor), ("name", Name(actor)), ("ip", Ip(actor)));
                await ExecuteAsync(conn, """
                    INSERT INTO read_access_log (id, tenant_id, endpoint, status_code, actor_subject_id, actor_name, ip_address, user_agent, created_at)
                    VALUES (gen_random_uuid(), @tenant, 'GET /api/v4/sensor-glucoses', 200, @actor, @name, @ip, @agent, now())
                    """, ("tenant", tenant), ("actor", actor), ("name", Name(actor)), ("ip", Ip(actor)), ("agent", Agent(actor)));
            }
        }
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_ClearsTheSubjectsClientInEveryTenant(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            var mutation = (await MutationRowsAsync(tenant))[_leaver];
            mutation.Ip.Should().BeNull($"tenant {tenant} holds the deleted subject's mutation row");

            var read = (await ReadRowsAsync(tenant))[_leaver];
            read.Ip.Should().BeNull($"tenant {tenant} holds the deleted subject's read row");
            read.Agent.Should().BeNull($"tenant {tenant} holds the deleted subject's read row");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_KeepsTheSubjectsNameAndId(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            (await MutationRowsAsync(tenant))[_leaver].Name.Should().Be(Name(_leaver));
            (await ReadRowsAsync(tenant))[_leaver].Name.Should().Be(Name(_leaver));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_LeavesAnotherActorsRowsUntouched(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            (await MutationRowsAsync(tenant))[_bystander]
                .Should().Be((Name(_bystander), (string?)Ip(_bystander)));
            (await ReadRowsAsync(tenant))[_bystander]
                .Should().Be((Name(_bystander), (string?)Ip(_bystander), (string?)Agent(_bystander)));
        }
    }

    /// <summary>
    /// Deletes as a reader with no tenant, as a platform admin on the apex does, or as a reader of the
    /// tenant the subject still belongs to, whose context the tenant query filter would hold there.
    /// </summary>
    private async Task DeleteLeaverAsync(bool fromTheMemberTenant)
    {
        await using var scope = _provider.CreateAsyncScope();
        if (fromTheMemberTenant)
            TenantReaderData.Resolve(scope.ServiceProvider, _memberTenant);

        var repository = new SubjectRepository(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>());
        (await repository.DeleteAsync(_leaver, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
    }

    private async Task<Dictionary<Guid, (string? Name, string? Ip)>> MutationRowsAsync(Guid tenant)
    {
        var rows = new Dictionary<Guid, (string?, string?)>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_subject_id, actor_name, ip_address FROM mutation_audit_log WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetGuid(0), (Text(reader, 1), Text(reader, 2)));
        return rows;
    }

    private async Task<Dictionary<Guid, (string? Name, string? Ip, string? Agent)>> ReadRowsAsync(Guid tenant)
    {
        var rows = new Dictionary<Guid, (string?, string?, string?)>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_subject_id, actor_name, ip_address, user_agent FROM read_access_log WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetGuid(0), (Text(reader, 1), Text(reader, 2), Text(reader, 3)));
        return rows;
    }

    private static string Name(Guid subject) => $"scrub-{subject:N}";

    private string Ip(Guid subject) => subject == _leaver ? "10.0.0.1" : "10.0.0.2";

    private static string Agent(Guid subject) => $"agent-{subject:N}";

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant) =>
        await ExecuteAsync(conn, "SELECT set_config('app.current_tenant_id', @tenant, false)", ("tenant", tenant.ToString()));

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
